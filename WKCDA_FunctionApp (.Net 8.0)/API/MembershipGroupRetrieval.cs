using DataverseModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.Tasks;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class MembershipGroupRetrieval : WS_Base
    {
        public MembershipGroupRetrieval(ILogger<MembershipGroupRetrieval> logger) : base(logger)
        {
        }

        [Function("MembershipGroupRetrieval")]
        public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/MembershipGroupRetrieval")] HttpRequest req)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();
                var listResults = new List<ResponseBody_Item>();

                foreach (var accountProfile in requestBody.accountProfiles)
                {
                    // MasterCustomerID required
                    if (string.IsNullOrWhiteSpace(accountProfile.MasterCustomerId))
                    {
                        listResults.Add(new ResponseBody_Item
                        {
                            Success = false,
                            Remarks = "Please provide MasterCustomerID",
                            MasterCustomerID = accountProfile.MasterCustomerId
                        });
                        continue;
                    }

                    var contact = RetrieveContactByMasterCustomerID(svc, accountProfile.MasterCustomerId);
                    if (contact == null)
                    {
                        listResults.Add(new ResponseBody_Item
                        {
                            Success = false,
                            Remarks = "Record does not exist",
                            MasterCustomerID = accountProfile.MasterCustomerId
                        });
                        continue;
                    }

                    // Retrieve membership tiers and cards
                    var mthList = GetMembershipTierHistory(svc, contact.Id, out bool needEligibilityCheck);
                    var consumedPercent = mthList.FirstOrDefault()?.ConsumedPercent ?? "0%";

                    listResults.Add(new ResponseBody_Item
                    {
                        Success = true,
                        Remarks = null,
                        MasterCustomerID = accountProfile.MasterCustomerId,
                        FirstName = accountProfile.FirstName,
                        LastName = accountProfile.LastName,
                        Email = accountProfile.Email,
                        MobilePhoneCountryCode = accountProfile.MobilePhoneCountryCode,
                        MobilePhoneNumber = accountProfile.MobilePhoneNumber,
                        MemberID = accountProfile.MemberId,
                        ConsumedPercent = consumedPercent,
                        mthList = mthList,
                        NeedEligibilityCheck = needEligibilityCheck
                    });
                }

                return new OkObjectResult(listResults);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new ResponseBody_Item
                {
                    Success = false,
                    Remarks = "Invalid JSON"
                });
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
                    { StatusCode = StatusCodes.Status401Unauthorized };
                }
                else
                {
                    _logger.LogError(ex, "Bad Request: {msg}", ex.Message);
                    return new ObjectResult(new
                    {
                        error = "Bad Request",
                        message = ex.Message
                    })
                    { StatusCode = StatusCodes.Status400BadRequest };
                }
            }
        }

        #region Mappings
        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                { "firstname", "firstname" },
                { "lastname", "lastname" },
                { "emailaddress1", "emailaddress1" },
                { "mobilephone", "mobilephone" },
                { "wkcda_mastercustomerid", "wkcda_mastercustomerid" },
                { "wkcda_memberid", "wkcda_memberid" }
            };
        }

        private ColumnSet GetColumnSetFromMapping()
        {
            return new ColumnSet(GetMappings().Values.ToArray());
        }
        #endregion

        #region Contact Retrieval
        private Entity RetrieveContactByMasterCustomerID(ServiceClient svc, string masterCustomerID)
        {
            var qe = new QueryExpression("contact")
            {
                ColumnSet = GetColumnSetFromMapping()
            };
            qe.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, masterCustomerID);
            return svc.RetrieveMultiple(qe).Entities.FirstOrDefault();
        }
        #endregion

        #region Membership Tier History
        private List<MembershipTierHistory> GetMembershipTierHistory(ServiceClient svc, Guid contactId, out bool needEligibilityCheck)
        {
            needEligibilityCheck = false; // initialize top-level flag
            var qeCards = new QueryExpression("wkcda_membershipcard")
            {
                ColumnSet = new ColumnSet("wkcda_membershiptierhistory", "wkcda_businessunit", "wkcda_cardnumber")
            };
            qeCards.Criteria.AddCondition("wkcda_member", ConditionOperator.Equal, contactId);

            var cards = svc.RetrieveMultiple(qeCards).Entities;
            var tierDict = new Dictionary<string, MembershipTierHistory>();

            foreach (var card in cards)
            {
                var tierHistoryRef = card.GetAttributeValue<EntityReference>("wkcda_membershiptierhistory");
                if (tierHistoryRef == null) continue;

                // Retrieve MembershipTierHistory record
                var tierHistory = svc.Retrieve("wkcda_membershiptierhistory", tierHistoryRef.Id,
                    new ColumnSet("wkcda_membershiptier", "wkcda_startdate", "wkcda_enddate", "wkcda_membershipconsumptionpercent", "wkcda_needeligibilitycheck"));

                // **Only retrieve NeedEligibilityCheck for top-level**
                if (tierHistory.GetAttributeValue<bool?>("wkcda_needeligibilitycheck") == true)
                {
                    needEligibilityCheck = true;
                }

                var tierRef = tierHistory.GetAttributeValue<EntityReference>("wkcda_membershiptier");
                string tierName = string.Empty;
                if (tierRef != null)
                {
                    var tier = svc.Retrieve("wkcda_membershiptier", tierRef.Id, new ColumnSet("wkcda_membershiptiername"));
                    tierName = tier.GetAttributeValue<string>("wkcda_membershiptiername") ?? string.Empty;
                }

                if (!tierDict.ContainsKey(tierHistoryRef.Id.ToString()))
                {
                    tierDict[tierHistoryRef.Id.ToString()] = new MembershipTierHistory
                    {
                        MemberTierHistoryId = tierHistoryRef.Id.ToString(),
                        MemberTierName = tierName,
                        MembershipStartDate = tierHistory.GetAttributeValue<DateTime?>("wkcda_startdate")?.ToString("yyyy-MM-dd"),
                        MembershipEndDate = tierHistory.GetAttributeValue<DateTime?>("wkcda_enddate")?.ToString("yyyy-MM-dd"),
                        BusinessUnit = card.GetAttributeValue<string>("wkcda_businessunit") ?? string.Empty,
                        ConsumedPercent = tierHistory.GetAttributeValue<string>("wkcda_consumedpercent") ?? "0%",
                        mCardList = new List<MembershipCardDetails>() // card details only
                    };
                }

                tierDict[tierHistoryRef.Id.ToString()].mCardList.Add(new MembershipCardDetails
                {
                    CardNumber = card.GetAttributeValue<string>("wkcda_cardnumber") ?? string.Empty,
                    ExpiryDate = card.GetAttributeValue<DateTime?>("wkcda_expirydate")?.ToString("yyyy-MM-dd") ?? string.Empty
                });
            }

            return tierDict.Values.ToList();
        }
        #endregion

        #region Request & Response
        public class RequestBody
        {
            public required AccountProfile[] accountProfiles { get; set; }
        }

        public class AccountProfile
        {
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string Email { get; set; }
            public string MobilePhoneCountryCode { get; set; }
            public string MobilePhoneNumber { get; set; }
            public string MemberId { get; set; }
            public string MasterCustomerId { get; set; }
            public string CardNumber { get; set; }
        }

        public class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public string MasterCustomerID { get; set; }
            public bool NeedEligibilityCheck { get; set; }
            public List<MembershipTierHistory> mthList { get; set; }
            public string MobilePhoneNumber { get; set; }
            public string MobilePhoneCountryCode { get; set; }
            public string MemberID { get; set; }
            public string LastName { get; set; }
            public string FirstName { get; set; } 
            public string Email { get; set; }
            public string ConsumedPercent { get; set; }
            
        }
        public class MembershipProfile
        {
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string Email { get; set; }
            public string MobilePhoneCountryCode { get; set; }
            public string MobilePhoneNumber { get; set; }
            public string MemberID { get; set; }
            public string MasterCustomerID { get; set; }
            public string CardNumber { get; set; }
            public string ConsumedPercent { get; set; }
            public List<MembershipTierHistory> mthList { get; set; }
        }

        public class MembershipTierHistory
        {
            public string MemberTierHistoryId { get; set; }
            public string MemberTierName { get; set; }
            public string MembershipStartDate { get; set; }
            public string MembershipEndDate { get; set; }
            public string ConsumedPercent { get; set; }
            public List<MembershipCardDetails> mCardList { get; set; }
            public string BusinessUnit { get; set; }
        }

        public class MembershipCardDetails
        {
            public string CardNumber { get; set; }
            public string ExpiryDate { get; set; }
        }
        #endregion
    }
}
