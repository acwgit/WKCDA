using System.Security.Authentication;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using WKCDA_FunctionApp__.Net_8._0_.Model;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using System.Text;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class MembershipRetrieval : WS_Base
    {
        public MembershipRetrieval(ILogger<MembershipRetrieval> logger) : base(logger) { }

        [Function("MembershipRetrieval")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/MembershipRetrieval")] HttpRequest req)
        {
            _logger.LogInformation("MembershipRetrieval function started.");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.accountProfile == null)
                {
                    return new BadRequestObjectResult(new { Success = false, Remarks = "Invalid request body" });
                }

                var profile = requestBody.accountProfile;
                if (string.IsNullOrWhiteSpace(profile.MasterCustomerId))
                {
                    return new OkObjectResult(new[]
                    {
                        new ResponseBody_Item
                        {
                            Success = false,
                            Remarks = "Please provide MasterCustomerID.",
                            MasterCustomerID = profile.MasterCustomerId
                        }
                    });
                }
                // Get Contact via Membership Card
                var contact = GetContactByMembershipCard(svc, profile);
                if (contact == null)
                    return new OkObjectResult(new[]
                    {
                        new ResponseBody_Item
                        {
                            Success = false,
                            Remarks = "Customer not found.",
                            MasterCustomerID = profile.MasterCustomerId
                        }
                    });
                // decode email and mobile
                var decodedEmail = DecodeBase64Field(contact.GetAttributeValue<string>("emailaddress1"));
                var decodedMobile = DecodeBase64Field(contact.GetAttributeValue<string>("mobilephone"));
                // Map CRM → API Response
                var response = new List<ResponseBody_Item>
                {
                    new ResponseBody_Item
                    {
                        Success = true,
                        Remarks = null,
                        MasterCustomerID = contact.GetAttributeValue<string>("wkcda_mastercustomerid"),
                        MobilePhoneNumber = decodedMobile,
                        MobilePhoneCountryCode = contact.GetAttributeValue<string>("wkcda_mobilecountrycode"),
                        MemberID = contact.GetAttributeValue<string>("wkcda_memberid"),
                        LastName = contact.GetAttributeValue<string>("lastname"),
                        FirstName = contact.GetAttributeValue<string>("firstname"),
                        Email = decodedEmail,
                        ConsumedPercent = null,

                        mthList = GetMembershipHistory(svc, contact.Id)
                    }
                };

                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new { Success = false, Remarks = "Invalid JSON" });
            }
            catch (AuthenticationException ex)
            {
                return new UnauthorizedObjectResult(ex.Message);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("unauthorized") || ex.Message.Contains("authorization failed"))
                {
                    return new ObjectResult(new
                    {
                        error = "Session expired or invalid",
                        message = ex.Message
                    })
                    {
                        StatusCode = StatusCodes.Status401Unauthorized
                    };
                }
                else
                {
                    return new ObjectResult(new
                    {
                        error = "Bad Request",
                        message = ex.Message
                    })
                    {
                        StatusCode = StatusCodes.Status400BadRequest
                    };
                }

            }
        }
        private string DecodeBase64Field(string encodedValue)
        {
            if (string.IsNullOrWhiteSpace(encodedValue))
                return string.Empty;

            try
            {
                if (IsBase64String(encodedValue))
                {
                    byte[] data = Convert.FromBase64String(encodedValue);
                    string decodedString = Encoding.UTF8.GetString(data);

                    _logger.LogInformation($"Successfully decoded Base64: {encodedValue} -> {decodedString}");
                    return decodedString;
                }
                else
                {
                    _logger.LogInformation($"Not a Base64 string, returning as-is: {encodedValue}");
                    return encodedValue;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error decoding Base64 field value: {encodedValue}");
                return encodedValue;
            }
        }
        private bool IsBase64String(string base64)
        {
            if (string.IsNullOrEmpty(base64) || base64.Length % 4 != 0)
                return false;

            try
            {
                byte[] data = Convert.FromBase64String(base64);

                // Extra validation: check if decoded string is valid UTF-8
                string decoded = Encoding.UTF8.GetString(data);
                foreach (char c in decoded)
                {
                    if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                { "" ,"" },
            };
        }
        private Entity GetContactByMembershipCard(ServiceClient svc, AccountProfile profile)
        {
            // Query the membership card
            var qeCard = new QueryExpression("wkcda_membershipcard")
            {
                ColumnSet = new ColumnSet("wkcda_member", "wkcda_cardnumber"),
                TopCount = 1
            };

            if (!string.IsNullOrEmpty(profile.MasterCustomerId))
            {
                // filter by MasterCustomerId on the Contact via linked entity
                var link = qeCard.AddLink("contact", "wkcda_member", "contactid", JoinOperator.Inner);
                link.Columns = new ColumnSet("wkcda_mastercustomerid");
                link.EntityAlias = "contact";
                link.LinkCriteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, profile.MasterCustomerId);
            }
            else if (!string.IsNullOrEmpty(profile.CardNumber))
            {
                qeCard.Criteria.AddCondition("wkcda_cardnumber", ConditionOperator.Equal, profile.CardNumber);
            }

            var cardResult = svc.RetrieveMultiple(qeCard);
            var card = cardResult.Entities.FirstOrDefault();
            if (card == null)
                return null;

            var contactRef = card.GetAttributeValue<EntityReference>("wkcda_member");
            if (contactRef == null)
                return null;

            // Retrieve Contact from the lookup to get MasterCustomerId
            return svc.Retrieve("contact", contactRef.Id, new ColumnSet(
                "firstname", "lastname", "emailaddress1",
                "mobilephone", "wkcda_mobilecountrycode",
                "wkcda_mastercustomerid", "wkcda_memberid"
            ));
        }


        private List<MemberTierHistory> GetMembershipHistory(ServiceClient svc, Guid contactId)
        {
            var histories = new List<MemberTierHistory>();

            // Retrieve all MembershipTierHistory for this contact
            var qe = new QueryExpression("wkcda_membershiptierhistory")
            {
                ColumnSet = new ColumnSet(
                    "wkcda_membershiphistoryname",
                    "wkcda_startdate",
                    "wkcda_enddate",
                    "wkcda_membershipconsumptionpercent",
                    "wkcda_member",
                    "wkcda_renewed",
                    "wkcda_membershiptier"
                ),
                Criteria =
        {
            Conditions =
            {
                new ConditionExpression("wkcda_member", ConditionOperator.Equal, contactId)
            }
        }
            };

            // Correct Link to MembershipTier
            var leTier = qe.AddLink(
                "wkcda_membershiptier",       // target entity
                "wkcda_membershiptier",       // lookup field on MembershipTierHistory
                "wkcda_membershiptierid",     // primary key on MembershipTier
                JoinOperator.LeftOuter);
            leTier.EntityAlias = "tier";
            leTier.Columns = new ColumnSet("wkcda_membershiptiername", "wkcda_businessunit");

            var result = svc.RetrieveMultiple(qe);

            foreach (var history in result.Entities)
            {
                string memberTierName = null;
                string businessUnitLabel = null;
                Guid? memberTierId = history.GetAttributeValue<EntityReference>("wkcda_membershiptier")?.Id;

                if (memberTierId != null)
                {
                    // Retrieve the MembershipTier record
                    var tierEntity = svc.Retrieve("wkcda_membershiptier", memberTierId.Value,
                        new ColumnSet("wkcda_membershiptiername", "wkcda_businessunit"));

                    memberTierName = tierEntity.GetAttributeValue<string>("wkcda_membershiptiername");

                    var businessUnitOptionSet = tierEntity.GetAttributeValue<OptionSetValue>("wkcda_businessunit");
                    if (businessUnitOptionSet != null)
                    {
                        // Retrieve OptionSet metadata to get the label
                        var attrRequest = new Microsoft.Xrm.Sdk.Messages.RetrieveAttributeRequest
                        {
                            EntityLogicalName = "wkcda_membershiptier",
                            LogicalName = "wkcda_businessunit",
                            RetrieveAsIfPublished = true
                        };
                        var attrResponse = (Microsoft.Xrm.Sdk.Messages.RetrieveAttributeResponse)svc.Execute(attrRequest);
                        var picklistAttr = (Microsoft.Xrm.Sdk.Metadata.PicklistAttributeMetadata)attrResponse.AttributeMetadata;

                        var option = picklistAttr.OptionSet.Options
                            .FirstOrDefault(o => o.Value == businessUnitOptionSet.Value);
                        businessUnitLabel = option?.Label?.UserLocalizedLabel?.Label;
                    }
                }
                //var memberTierHistoryRef = history.ToEntityReference(); // or EntityReference using history.Id

                var mth = new MemberTierHistory
                {
                    MemberTierHistoryId = history.GetAttributeValue<string>("wkcda_membershiphistoryname"),
                    MemberTierName = memberTierName,
                    MemberTierId = memberTierId?.ToString(),
                    MembershipStartDate = history.GetAttributeValue<DateTime?>("wkcda_startdate")?.ToString("yyyy-MM-dd"),
                    MembershipEndDate = history.GetAttributeValue<DateTime?>("wkcda_enddate")?.ToString("yyyy-MM-dd"),
                    Renewable = history.GetAttributeValue<bool?>("wkcda_renewed"),
                    BusinessUnit = businessUnitLabel,
                    MembershipConsumptionPercent = history.GetAttributeValue<double?>("wkcda_membershipconsumptionpercent"),
                    PaymentAmount = 0,
                    TotalAmount = 0,
                    NeedEligibilityCheck = false,
                    mCardList = new List<Card>(),
                    voucherList = new List<object>()
                };

                // Payment Transactions
                var payments = svc.RetrieveMultiple(new QueryExpression("wkcda_paymenttransaction")
                {
                    ColumnSet = new ColumnSet("wkcda_paymentamount", "wkcda_totalamount"),
                    Criteria =
                    {
                        Conditions =
                        {
                            new ConditionExpression("wkcda_membershiptierhistory", ConditionOperator.Equal, history.Id)
                        }
                    }
                });
                if (payments.Entities.Count > 0)
                {
                    mth.PaymentAmount = payments.Entities.Sum(p => p.GetAttributeValue<Money>("wkcda_paymentamount")?.Value ?? 0);
                    mth.TotalAmount = payments.Entities.Sum(p => p.GetAttributeValue<Money>("wkcda_totalamount")?.Value ?? 0);
                }

                // Membership Vouchers
                var vouchers = svc.RetrieveMultiple(new QueryExpression("wkcda_membershipvoucher")
                {
                    ColumnSet = new ColumnSet(
                        "wkcda_vouchercode", "wkcda_membershipvoucherreferencename", "wkcda_codestatus", "wkcda_initialactivationdate",
                        "wkcda_usetype", "wkcda_membershipbenefits", "wkcda_startdate", "wkcda_enddate",
                        "wkcda_discount", "wkcda_benefittype", "wkcda_category", "wkcda_latestactivationdate",
                        "wkcda_usedtimes", "wkcda_thumbnailurl", "wkcda_hidevoucherinmobileappcustomerpor"
                    ),
                    Criteria =
                    {
                        Conditions =
                        {
                            new ConditionExpression("wkcda_membershiptierhistory", ConditionOperator.Equal, history.Id)
                        }
                    }
                });
                mth.voucherList = vouchers.Entities.Select(v => new
                {
                    VoucherCode = v.GetAttributeValue<string>("wkcda_vouchercode"),
                    ReferenceNo = v.GetAttributeValue<string>("wkcda_membershipvoucherreferencename"),
                    CodeStatus = v.GetAttributeValue<string>("wkcda_codestatus"),
                    ActivationDate = v.GetAttributeValue<DateTime?>("wkcda_initialactivationdate")?.ToString("yyyy-MM-dd"),
                    UseType = v.GetAttributeValue<string>("wkcda_usetype"),
                    VoucherName = v.GetAttributeValue<EntityReference>("wkcda_membershipbenefits")?.Name,
                    StartDate = v.GetAttributeValue<DateTime?>("wkcda_startdate")?.ToString("yyyy-MM-dd"),
                    EndDate = v.GetAttributeValue<DateTime?>("wkcda_enddate")?.ToString("yyyy-MM-dd"),
                    Discount = v.GetAttributeValue<double?>("wkcda_discount"),
                    BenefitType = v.GetAttributeValue<string>("wkcda_benefittype"),
                    Category = v.GetAttributeValue<string>("wkcda_category"),
                    LatestActivationDate = v.GetAttributeValue<DateTime?>("wkcda_latestactivationdate")?.ToString("yyyy-MM-dd"),
                    UsedTimes = v.GetAttributeValue<double?>("wkcda_usedtimes"),
                    ThumbnailURL = v.GetAttributeValue<string>("wkcda_thumbnailurl"),
                    HideVoucherInMobileAppCustomerPortal = v.GetAttributeValue<bool?>("wkcda_hidevoucherinmobileappcustomerpor")
                }).Cast<object>().ToList();
                // Membership Cards

                var cards = svc.RetrieveMultiple(new QueryExpression("wkcda_membershipcard")
                {
                    ColumnSet = new ColumnSet("wkcda_cardnumber", "wkcda_cardstatus"),
                    Criteria =
                    {
                        Conditions =
                        {
                            new ConditionExpression("wkcda_member", ConditionOperator.Equal, contactId)
                        }
                    }
                });

                mth.mCardList = cards.Entities.Select(c => new Card
                {
                    CardNumber = c.GetAttributeValue<string>("wkcda_cardnumber"),
                    CardStatus = c.GetAttributeValue<string>("wkcda_cardstatus")
                }).ToList();
                // Handle Renewed Membership
                if (mth.Renewable == true)
                {
                    var renewedHistory = GetMembershipHistory(svc, contactId)
                                         .FirstOrDefault(r => r.MembershipStartDate == mth.MembershipEndDate);
                    if (renewedHistory != null)
                    {
                        mth.RenewedMTH = new RenewedMTH
                        {
                            MemberTierHistoryId = renewedHistory.MemberTierHistoryId,
                            MemberTierName = renewedHistory.MemberTierName,
                            MemberTierId = renewedHistory.MemberTierId,
                            MembershipStartDate = renewedHistory.MembershipStartDate,
                            MembershipEndDate = renewedHistory.MembershipEndDate,
                            PaymentAmount = renewedHistory.PaymentAmount,
                            TotalAmount = renewedHistory.TotalAmount,
                            BusinessUnit = renewedHistory.BusinessUnit,
                            MembershipConsumptionPercent = renewedHistory.MembershipConsumptionPercent,
                            Renewable = renewedHistory.Renewable,
                            mCardList = renewedHistory.mCardList,
                            voucherList = renewedHistory.voucherList,
                            NeedEligibilityCheck = renewedHistory.NeedEligibilityCheck,
                            RenewedMTH = renewedHistory.RenewedMTH,
                            RenewedMembershipStartDate = renewedHistory.RenewedMembershipStartDate,
                            RenewedMembershipEndDate = renewedHistory.RenewedMembershipEndDate
                        };
                    }
                }

                histories.Add(mth);
            }

            return histories;
        }
/*        // Serializer to JSON for output
        public string GetMembershipHistoryJson(ServiceClient svc, Guid contactId, string masterCustomerId,
            string firstName, string lastName, string email, string mobileNumber, string countryCode, string memberId)
        {
            var mthList = GetMembershipHistory(svc, contactId);

            var output = new
            {
                Success = true,
                Remarks = (string)null,
                MasterCustomerID = masterCustomerId,
                mthList = mthList,
                MobilePhoneNumber = mobileNumber,
                MobilePhoneCountryCode = countryCode,
                MemberID = memberId,
                LastName = lastName,
                FirstName = firstName,
                Email = email,
                ConsumedPercent = (double?)null
            };

            return JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
        }*/


        #region Request/Response Models

        class RequestBody
        {
            public required AccountProfile accountProfile { get; set; }
        }
        class AccountProfile
        {
            public string? FirstName { get; set; }
            public string? LastName { get; set; }
            public string? Email { get; set; }
            public string? MobilePhoneCountryCode { get; set; }
            public string? MobilePhoneNumber { get; set; }
            public string? MemberId { get; set; }
            public string? MasterCustomerId { get; set; }
            public string? CardNumber { get; set; }
            public bool Renewal { get; set; }
            public bool GetRenewed { get; set; }
        }
        class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string? Remarks { get; set; }
            public string? MasterCustomerID { get; set; }
            public List<MemberTierHistory>? mthList { get; set; }
            public string? MobilePhoneNumber { get; set; }
            public string? MobilePhoneCountryCode { get; set; }
            public string? MemberID { get; set; }
            public string? LastName { get; set; }
            public string? FirstName { get; set; }
            public string? Email { get; set; }
            public decimal? ConsumedPercent { get; set; }
        }
        class MemberTierHistory
        {
            public List<object>? voucherList { get; set; }
            public decimal TotalAmount { get; set; }
            public RenewedMTH? RenewedMTH { get; set; }
            public string? RenewedMembershipStartDate { get; set; }
            public string? RenewedMembershipEndDate { get; set; }
            public bool? Renewable { get; set; }
            public decimal PaymentAmount { get; set; }
            public bool NeedEligibilityCheck { get; set; }
            public string? MemberTierName { get; set; }
            public string? MemberTierId { get; set; }
            public string? MemberTierHistoryId { get; set; }
            public string? MembershipStartDate { get; set; }
            public string? MembershipEndDate { get; set; }
            public List<Card>? mCardList { get; set; }
            public string? BusinessUnit { get; set; }

            public double? MembershipConsumptionPercent { get; set; }
        }
        class RenewedMTH : MemberTierHistory { }

        class Card
        {
            public string? CardStatus { get; set; }
            public string? CardNumber { get; set; }
        }

        #endregion
    }
}