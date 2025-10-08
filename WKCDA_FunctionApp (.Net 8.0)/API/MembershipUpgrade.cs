using Microsoft.AspNetCore.Http;
using DataverseModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using WKCDA_FunctionApp__.Net_8._0_.Model;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using System.Linq;
using Grpc.Core;
using System.Security.Authentication;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class MembershipUpgrade : WS_Base
    {
        public MembershipUpgrade(ILogger<MembershipUpgrade> logger) : base(logger) { }

        [Function("MembershipUpgrade")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/MembershipUpgrade")] HttpRequest req)
        {
            _logger.LogInformation("APIName: MembershipUpgrade");

            try
            {
                string requestBodyString = await new StreamReader(req.Body).ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(requestBodyString))
                {
                    return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Empty request body" } });
                }

              //  _logger.LogInformation($"Request body: {requestBodyString}");


                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                };

                var requestBody = JsonSerializer.Deserialize<RequestBody>(requestBodyString, options);

                if (requestBody?.customerEntity?.AccountProfile == null || requestBody.customerEntity.AccountProfile.Count == 0)
                    return new OkObjectResult(new { Success = false, Remarks = "Invalid request body" });

                if (requestBody.customerEntity.Login.Equals(false))
                {
                    return new OkObjectResult(new List<ResponseBody_Item>
                {
                    new ResponseBody_Item { Success = false, Remarks = "Please log in your MyWestKowloon Account to continue." }
                });
                }

                var response = new List<ResponseBody_Item>();

                foreach (var account in requestBody.customerEntity.AccountProfile)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(account.MemberTierHistoryId))
                        {
                            response.Add(new ResponseBody_Item { Success = false, Remarks = "MemberTierHistoryId is required" });
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(account.Email))
                        {
                            response.Add(new ResponseBody_Item { Success = false, Remarks = "Email is required" });
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(account.PaidBy))
                        {
                            response.Add(new ResponseBody_Item { Success = false, Remarks = "PaidBy is required" });
                            continue;
                        }

                        var svc = GetServiceClient(req);

                        var currentMth = GetCurrentMembershipTierHistory(svc, account.MemberTierHistoryId);
                        if (currentMth == null)
                        {
                            response.Add(new ResponseBody_Item { Success = false, Remarks = $"MembershipTierHistory with ID {account.MemberTierHistoryId} not found or not active" });
                            continue;
                        }

                        var paidByContactRef = GetContactReference(svc, account.PaidBy);
                        if (paidByContactRef == null)
                        {
                            response.Add(new ResponseBody_Item { Success = false, Remarks = $"No matching contact for PaidBy email: {account.PaidBy}" });
                            continue;
                        }

                        var emailCheck = GetContactReference(svc, account.Email);
                        if (emailCheck == null)
                        {
                            response.Add(new ResponseBody_Item { Success = false, Remarks = $"No matching record for Email: {account.Email}" });
                            continue;
                        }

                        UpdateCurrentMembershipForUpgrade(svc, currentMth);

                        var refundPaymentId = CreateRefundPaymentTransaction(svc, account, paidByContactRef.Id, currentMth.Id);

                        var tempMemberTierHistoryId = account.MemberTierHistoryId;
                        account.MemberTierHistoryId = null;

                        var upgradeResult = await ProcessMembershipUpgrade(svc, account, paidByContactRef.Id);

                        if (upgradeResult.Success)
                        {
                            response.Add(new ResponseBody_Item
                            {
                                Success = true,
                                Remarks = account.Email,
                                MasterCustomerID = upgradeResult.MasterCustomerID,
                                PaymentHistoryId = upgradeResult.PaymentHistoryId,
                                MemberTierHistoryId = upgradeResult.MemberTierHistoryId,
                                MembershipStartDate = upgradeResult.MembershipStartDate,
                                MembershipEndDate = upgradeResult.MembershipEndDate,
                                Renewal = upgradeResult.Renewal,
                                Reference = account.ReferenceNo
                            });
                        }
                        else
                        {
                            response.Add(new ResponseBody_Item { Success = false, Remarks = upgradeResult.Remarks });
                        }
                    }
                    /*
                     *                     catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing account upgrade");
                        response.Add(new ResponseBody_Item { Success = false, Remarks = ex.Message });
                    }
                     */
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

                return new OkObjectResult(response);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON deserialization error");
                return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = $"Invalid JSON: {ex.Message}" } });
            }
            /*  catch (Exception ex)
              {
                  _logger.LogError(ex, "Unexpected error");
                  return new ObjectResult(new { error = "Bad Request", message = ex.Message }) { StatusCode = StatusCodes.Status400BadRequest };
              }*/
            catch (AuthenticationException ex)
            {
                return new UnauthorizedObjectResult(ex.Message);
            }
        }

        #region CRM Helpers

        private Entity GetCurrentMembershipTierHistory(ServiceClient svc, string memberTierHistoryId)
        {
            if (!Guid.TryParse(memberTierHistoryId, out Guid mthGuid))
                return null;

            var qe = new QueryExpression("wkcda_membershiptierhistory")
            {
                ColumnSet = new ColumnSet("wkcda_membershiptierhistoryid", "wkcda_enddate", "statuscode")
            };
            qe.Criteria.AddCondition("wkcda_membershiptierhistoryid", ConditionOperator.Equal, mthGuid);
            qe.Criteria.AddCondition("statuscode", ConditionOperator.Equal, 1); // Active status

            var result = svc.RetrieveMultiple(qe);
            return result.Entities.Count > 0 ? result.Entities[0] : null;
        }

        private void UpdateCurrentMembershipForUpgrade(ServiceClient svc, Entity currentMth)
        {
            var originalEndDate = currentMth.GetAttributeValue<DateTime>("wkcda_enddate");
            if (originalEndDate < new DateTime(1753, 1, 1))
            {
                originalEndDate = DateTime.Today;
            }

            var updateEntity = new Entity("wkcda_membershiptierhistory", currentMth.Id)
            {
                ["wkcda_originalenddate"] = originalEndDate,
                ["wkcda_terminationdate"] = DateTime.Now,
                ["wkcda_enddate"] = DateTime.Today,
                ["wkcda_upgraded"] = true
            };

            svc.Update(updateEntity);
        }

        private Guid CreateRefundPaymentTransaction(ServiceClient svc, AccountProfile account, Guid paidByContactId, Guid membershipTierHistoryId)
        {
            var entity = new Entity("wkcda_paymenttransaction")
            {
                ["wkcda_membershiptierhistory"] = new EntityReference("wkcda_membershiptierhistory", membershipTierHistoryId),
                ["wkcda_totalamount"] = new Money(SafeDecimalParse(account.RefundTotalAmount)),
                ["wkcda_paymentamount"] = new Money(SafeDecimalParse(account.RefundPaymentAmount)),
                ["wkcda_discountamount"] = new Money(SafeDecimalParse(account.RefundDiscountAmount)),
                ["wkcda_refundamount"] = new Money(SafeDecimalParse(account.RefundAmount)),
                ["wkcda_terminationdate"] = DateTime.Today,
                ["wkcda_transactiontype"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_transactiontype", "Refund", _logger) ?? 0),
                ["wkcda_paymentdate"] = DateTime.Parse(account.PaymemtDate),
                ["wkcda_transactionno"] = account.RefundPaymentGatewayTransactionNumber,
                ["wkcda_paymentremarks"] = account.RefundApprovalCode ?? string.Empty,
                ["statuscode"] = new OptionSetValue(1), // Succeeded
            };


            if (!string.IsNullOrWhiteSpace(account.PaymentType))
            {
                entity["wkcda_paymenttype"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_paymenttype", account.PaymentType, _logger) ?? 0);
            }

            if (!string.IsNullOrWhiteSpace(account.SalesChannel))
            {
                entity["wkcda_saleschannel"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_saleschannel", account.SalesChannel, _logger) ?? 0);
            }

            return svc.Create(entity);
        }

        private async Task<UpgradeResult> ProcessMembershipUpgrade(ServiceClient svc, AccountProfile account, Guid paidByContactId)
        {
            try
            {
                var contactId = EnsureContact(svc, account);

                var paymentHistoryId = CreateUpgradePaymentTransaction(svc, contactId, account, paidByContactId);

                var memberTierHistoryId = CreateMembershipTierHistory(svc, contactId, account);

                if (account.SubscriptionInfos != null)
                {
                    UpdateSubscriptions(svc, contactId, account.SubscriptionInfos);
                }

                if (account.ConsentInfos != null)
                {
                    UpdateConsents(svc, contactId, account.ConsentInfos);
                }

                var startDate = DateTime.Today;
                var endDate = startDate.AddYears(1);

                var existingContact = svc.Retrieve("contact", contactId, new ColumnSet("wkcda_mastercustomerid"));
                var masterCustomerId = existingContact.GetAttributeValue<string>("wkcda_mastercustomerid");

                return new UpgradeResult
                {
                    Success = true,
                    PaymentHistoryId = paymentHistoryId.ToString(),
                    MemberTierHistoryId = memberTierHistoryId.ToString(),
                    MasterCustomerID = masterCustomerId,
                    MembershipStartDate = startDate.ToString("yyyy-MM-dd"),
                    MembershipEndDate = endDate.ToString("yyyy-MM-dd"),
                    Renewal = false
                };
            }
            catch (Exception ex)
            {
                return new UpgradeResult
                {
                    Success = false,
                    Remarks = ex.Message
                };
            }
        }

        private Guid EnsureContact(ServiceClient svc, AccountProfile account)
        {
            var contactRef = GetContactReference(svc, account.Email);
            Guid contactId;

            if (contactRef != null)
            {
                contactId = contactRef.Id;

                var contactEntity = new Entity("contact", contactId);
                if (!string.IsNullOrWhiteSpace(account.Email))
                    contactEntity["emailaddress1"] = account.Email;
                if (!string.IsNullOrWhiteSpace(account.MobilePhoneNumber))
                    contactEntity["mobilephone"] = account.MobilePhoneNumber;

                svc.Update(contactEntity);
            }
            else
            {
                var contactEntity = new Entity("contact");
                if (!string.IsNullOrWhiteSpace(account.FirstName))
                    contactEntity["firstname"] = account.FirstName;
                if (!string.IsNullOrWhiteSpace(account.LastName))
                    contactEntity["lastname"] = account.LastName;
                if (!string.IsNullOrWhiteSpace(account.Email))
                    contactEntity["emailaddress1"] = account.Email;
                if (!string.IsNullOrWhiteSpace(account.MobilePhoneNumber))
                    contactEntity["mobilephone"] = account.MobilePhoneNumber;

                // Generate new master customer id
                contactEntity["wkcda_mastercustomerid"] = $"P{DateTime.UtcNow.Ticks}";

                contactId = svc.Create(contactEntity);
            }

            return contactId;
        }


        private Guid CreateUpgradePaymentTransaction(ServiceClient svc, Guid contactId, AccountProfile account, Guid paidByContactId)
        {
            var entity = new Entity("wkcda_paymenttransaction")
            {
                ["wkcda_totalamount"] = new Money(SafeDecimalParse(account.TotalAmount)),
                ["wkcda_paymentamount"] = new Money(SafeDecimalParse(account.PaymentAmount)),
                ["wkcda_discountamount"] = new Money(SafeDecimalParse(account.DiscountAmount)),
                ["wkcda_paymentdate"] = SafeDateTimeParse(account.PaymemtDate),
                ["wkcda_paidby"] = new EntityReference("contact", paidByContactId),
                // ["wkcda_accountname"] = new EntityReference("contact", contactId),
                ["statuscode"] = new OptionSetValue(1) // Succeeded
            };

            if (!string.IsNullOrWhiteSpace(account.PaymentType))
            {
                entity["wkcda_paymenttype"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_paymenttype", account.PaymentType, _logger) ?? 0);
            }

            if (!string.IsNullOrWhiteSpace(account.SalesChannel))
            {
                entity["wkcda_saleschannel"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_saleschannel", account.SalesChannel, _logger) ?? 0);
            }

            if (!string.IsNullOrWhiteSpace(account.PaymentGatewayTransactionNumber))
            {
                entity["wkcda_transactionno"] = account.PaymentGatewayTransactionNumber;
            }

            if (!string.IsNullOrWhiteSpace(account.ApprovalCode))
            {
                entity["wkcda_paymentremarks"] = account.ApprovalCode;
            }

            return svc.Create(entity);
        }

        private Guid CreateMembershipTierHistory(ServiceClient svc, Guid contactId, AccountProfile account)
        {
            var query = new QueryExpression("wkcda_membershiptier")
            {
                ColumnSet = new ColumnSet("wkcda_membershiptierid"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wkcda_membershiptiername", ConditionOperator.Equal, account.MembershipTier)
                    }
                }
            };

            var tier = svc.RetrieveMultiple(query).Entities.FirstOrDefault();
            if (tier == null)
            {
                throw new InvalidPluginExecutionException($"Membership Tier '{account.MembershipTier}' not found.");
            }

            var entity = new Entity("wkcda_membershiptierhistory")
            {
                ["wkcda_member"] = new EntityReference("contact", contactId),
                ["wkcda_membershiptier"] = new EntityReference("wkcda_membershiptier", tier.Id),
                ["wkcda_startdate"] = DateTime.Today,
                ["wkcda_enddate"] = DateTime.Today.AddYears(1)
            };
            return svc.Create(entity);
        }

        private void UpdateSubscriptions(ServiceClient svc, Guid contactId, List<SubscriptionInfo> subs)
        {
            var entity = new Entity("contact", contactId);

            foreach (var sub in subs)
            {
                bool value = !sub.IsOptOut;

                switch (sub.SubscriptionType.ToLower())
                {
                    case "westkowloonenewsletter":
                        entity["wkcda_westkowloonenewsletter"] = value;
                        break;
                    case "mmagazine":
                        entity["wkcda_mmagazine"] = value;
                        break;
                    case "mmembershipenews":
                    case "mplusmembership":
                        entity["wkcda_mmembershipenews"] = value;
                        break;
                    case "hkpmmembership":
                        entity["wkcda_hkpmmembershipenews"] = value;
                        break;
                    case "mshop":
                        entity["wkcda_mshop"] = value;
                        break;
                    case "mliedm":
                    case "mplus":
                        entity["wkcda_mliedm"] = value;
                        break;
                }
            }

            svc.Update(entity);
        }

        private void UpdateConsents(ServiceClient svc, Guid contactId, List<ConsentInfo> consents)
        {
            var qe = new QueryExpression("wkcda_membershiptierhistory")
            {
                ColumnSet = new ColumnSet("wkcda_membershiptierhistoryid", "wkcda_optindateanonymous", "wkcda_optinchannelanonymous",
                                         "wkcda_optindatecollateral", "wkcda_optinchannelcollateral",
                                         "wkcda_optindateguardian", "wkcda_optinchannelguardian")
            };
            qe.Criteria.AddCondition("wkcda_member", ConditionOperator.Equal, contactId);
            qe.Criteria.AddCondition("statuscode", ConditionOperator.Equal, 1);

            var tierHist = svc.RetrieveMultiple(qe).Entities.FirstOrDefault();
            if (tierHist != null)
            {
                /* foreach (var consent in consents)
                 {
                     switch (consent.ConsentType.ToLower())
                     {
                         case "collateral":
                             tierHist["wkcda_optindatecollateral"] = SafeDateTimeParse(consent.ConsentDate);
                             tierHist["wkcda_optinchannelcollateral"] = consent.ConsentSource;
                             break;
                         case "guardian":
                             tierHist["wkcda_optindateguardian"] = SafeDateTimeParse(consent.ConsentDate);
                             tierHist["wkcda_optinchannelguardian"] = consent.ConsentSource;
                             break;
                         case "anonymous":
                             tierHist["wkcda_optindateanonymous"] = SafeDateTimeParse(consent.ConsentDate);
                             tierHist["wkcda_optinchannelanonymous"] = consent.ConsentSource;
                             break;
                     }
                 }*/
                svc.Update(tierHist);
            }
        }

        private EntityReference? GetContactReference(ServiceClient svc, string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;

            var qe = new QueryExpression("contact") { ColumnSet = new ColumnSet("contactid") };
            qe.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, email);

            var ec = svc.RetrieveMultiple(qe);
            return ec.Entities.Count > 0 ? ec.Entities[0].ToEntityReference() : null;
        }

        /* private void CreatePICS(ServiceClient svc, Guid contactId, PICSInfo pics)
         {
             var entity = new Entity("wkcda_picsinfo")
             {
                 ["wkcda_contact"] = new EntityReference("contact", contactId),
                 ["wkcda_picstype"] = pics.PICSType,
                 ["wkcda_picsdate"] = SafeDateTimeParse(pics.PICSDate),
                 ["wkcda_source"] = pics.PICSSource
             };
             svc.Create(entity);
         }*/

        private decimal SafeDecimalParse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0m;
            if (decimal.TryParse(value, out decimal result)) return result;
            return 0m;
        }

        private DateTime SafeDateTimeParse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DateTime.Today;
            if (DateTime.TryParse(value, out DateTime result))
            {
                if (result < new DateTime(1753, 1, 1))
                    return DateTime.Today;
                return result;
            }
            return DateTime.Today;
        }

        #endregion

        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                { "", "" },
            };
        }

        #region Request/Response Classes

        public class RequestBody
        {
            public CustomerEntity customerEntity { get; set; }
        }

        public class CustomerEntity
        {
            public string PromoCode { get; set; }
            public bool Login { get; set; }
            public List<AccountProfile> AccountProfile { get; set; }
        }

        public class AccountProfile
        {
            public string MemberTierHistoryId { get; set; }
            public string RefundAmount { get; set; }
            public string RefundTotalAmount { get; set; }
            public string RefundPaymentAmount { get; set; }
            public string RefundDiscountAmount { get; set; }
            public string RefundPaymentGatewayTransactionNumber { get; set; }
            public string RefundApprovalCode { get; set; }
            public string PaymentGatewayTransactionNumber { get; set; }
            public string ApprovalCode { get; set; }

            public string Email { get; set; }
            public string Salutation { get; set; }
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string MonthofBirth { get; set; }
            public string YearofBirth { get; set; }
            public string MobilePhoneCountryCode { get; set; }
            public string MobilePhoneNumber { get; set; }
            public string PreferredLanguage { get; set; }
            public string Password { get; set; }
            public string Address1 { get; set; }
            public string Address2 { get; set; }
            public string Address3 { get; set; }
            public string AddressCountry { get; set; }
            public string AltCntcFirstName { get; set; }
            public string AltCntcLastName { get; set; }
            public string AltCntcEmail { get; set; }
            public string AltCntcMobileCountryCode { get; set; }
            public string AltCntcMobile { get; set; }
            public string CustomerSource { get; set; }
            public bool IsSubsidaryAccount { get; set; }
            public string MembershipTier { get; set; }
            public string ReferenceNo { get; set; }
            public bool IsChild { get; set; }
            public bool IsStudent { get; set; }
            public string TotalAmount { get; set; }
            public string PaymentAmount { get; set; }
            public string DiscountAmount { get; set; }
            public string PaymentType { get; set; }
            public string SalesChannel { get; set; }
            public string PaymemtDate { get; set; }
            public string PaidBy { get; set; }
            public string MemberGroupType { get; set; }
            public string MemberGroupRole { get; set; }
            public List<SubscriptionInfo> SubscriptionInfos { get; set; }
            public List<ConsentInfo> ConsentInfos { get; set; }
            public List<PICSInfo> PICSInfos { get; set; }
        }

        public class SubscriptionInfo
        {
            public string SubscriptionType { get; set; }
            public string SubscriptionDate { get; set; }
            public string SubscriptionSource { get; set; }
            public bool IsOptOut { get; set; }
        }

        public class ConsentInfo
        {
            public string ConsentType { get; set; }
            public string ConsentDate { get; set; }
            public string ConsentSource { get; set; }
        }

        public class PICSInfo
        {
            public string PICSType { get; set; }
            public string PICSDate { get; set; }
            public string PICSSource { get; set; }
        }

        public class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public string MasterCustomerID { get; set; }
            public string PaymentHistoryId { get; set; }
            public string MemberTierHistoryId { get; set; }

            public string MembershipStartDate { get; set; }
            public string MembershipEndDate { get; set; }
            public bool Renewal { get; set; }
            public string Reference { get; set; }
            public bool PromoCode { get; set; }
            public string GroupRelationshipId { get; set; }
            public string GroupId { get; set; }
            public string ActivationCode { get; set; }

        }

        private class UpgradeResult
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public string PaymentHistoryId { get; set; }
            public string MemberTierHistoryId { get; set; }
            public string MasterCustomerID { get; set; }
            public string MembershipStartDate { get; set; }
            public string MembershipEndDate { get; set; }
            public bool Renewal { get; set; }
        }

        #endregion
    }
}