using Microsoft.AspNetCore.Http;
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
using WKCDA_FunctionApp__.Net_8._0_.Model;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using DataverseModel;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class MembershipTierCheckingWS : WS_Base
    {
        public MembershipTierCheckingWS(ILogger<MembershipTierCheckingWS> logger) : base(logger) { }

        [Function("MembershipTierCheckingWS")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/MembershipTierCheckingWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: MembershipTierCheckingWS");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.inputData == null)
                    return new OkObjectResult(new ResponseBody { Success = false, Remarks = "Invalid request body" });

                var result = ProcessMembershipTierCheck(svc, requestBody.inputData);
                return new OkObjectResult(result);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new ResponseBody { Success = false, Remarks = "Invalid JSON" });
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

        private ResponseBody ProcessMembershipTierCheck(ServiceClient svc, InputDataBean inputData)
        {
            var result = new ResponseBody { Success = true, Remarks = "Success" };

            // Validate required fields
            if (string.IsNullOrWhiteSpace(inputData.MembershipTier))
            {
                return ReturnError("MembershipTier is required!", result);
            }

            // Validate that at least one identifier is provided
            if (string.IsNullOrWhiteSpace(inputData.MemberID) && string.IsNullOrWhiteSpace(inputData.Email))
            {
                return ReturnError("Either MemberID or Email must be provided!", result);
            }

            try
            {
                // Validate membership tier exists
                var tierQuery = new QueryExpression("wkcda_membershiptier")
                {
                    ColumnSet = new ColumnSet("wkcda_membershiptiername"),
                    Criteria = new FilterExpression
                    {
                        Conditions =
                        {
                            new ConditionExpression("wkcda_membershiptiername", ConditionOperator.Equal, inputData.MembershipTier)
                        }
                    }
                };

                var tierResult = svc.RetrieveMultiple(tierQuery);
                if (tierResult.Entities.Count == 0)
                {
                    return ReturnError($"Membership tier '{inputData.MembershipTier}' not found.", result);
                }

                var tierId = tierResult.Entities[0].Id;
                Guid memberId = Guid.Empty;
                Entity contact = null;
                // Case 1: Both MemberID and Email provided
                if (!string.IsNullOrWhiteSpace(inputData.MemberID) && !string.IsNullOrWhiteSpace(inputData.Email))
                {
                    var memberQuery = new QueryExpression("contact")
                    {
                        ColumnSet = new ColumnSet("contactid", "wkcda_mastercustomerid", "emailaddress1"),
                        Criteria = new FilterExpression(LogicalOperator.And)
                    };

                    memberQuery.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, inputData.MemberID);
                    memberQuery.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, inputData.Email);

                    var memberResult = svc.RetrieveMultiple(memberQuery);
                    if (memberResult.Entities.Count == 0)
                    {
                        return ReturnError($"No member found with MemberID '{inputData.MemberID}' and Email '{inputData.Email}'. Please check if both identifiers match the same record.", result);
                    }

                    memberId = memberResult.Entities[0].Id;
                    contact = memberResult.Entities[0];
                }
                // Case 2: Only MemberID provided
                else if (!string.IsNullOrWhiteSpace(inputData.MemberID))
                {
                    var memberQuery = new QueryExpression("contact")
                    {
                        ColumnSet = new ColumnSet("contactid", "wkcda_mastercustomerid", "wkcda_memberid", "emailaddress1",
                                      "wkcda_wkmemobilephonecountrycode", "wkcda_wkmemobilephonenumber",
                                      "lastname", "firstname", "wkcda_status"),
                        Criteria = new FilterExpression
                        {
                            Conditions =
                            {
                                new ConditionExpression("wkcda_mastercustomerid", ConditionOperator.Equal, inputData.MemberID)
                            }
                        }
                    };

                    var memberResult = svc.RetrieveMultiple(memberQuery);
                    if (memberResult.Entities.Count == 0)
                    {
                        return ReturnError($"Member with ID '{inputData.MemberID}' not found.", result);
                    }

                    memberId = memberResult.Entities[0].Id;
                    contact = memberResult.Entities[0];
                }
                // Case 3: Only Email provided
                else if (!string.IsNullOrWhiteSpace(inputData.Email))
                {
                    var memberQuery = new QueryExpression("contact")
                    {
                        ColumnSet = new ColumnSet("contactid", "wkcda_mastercustomerid", "wkcda_memberid", "emailaddress1",
                                      "wkcda_wkmemobilephonecountrycode", "wkcda_wkmemobilephonenumber",
                                      "lastname", "firstname", "wkcda_status"),
                        Criteria = new FilterExpression
                        {
                            Conditions =
                            {
                                new ConditionExpression("emailaddress1", ConditionOperator.Equal, inputData.Email)
                            }
                        }
                    };

                    var memberResult = svc.RetrieveMultiple(memberQuery);
                    if (memberResult.Entities.Count == 0)
                    {
                        return ReturnError($"Member with email '{inputData.Email}' not found.", result);
                    }

                    memberId = memberResult.Entities[0].Id;
                    contact = memberResult.Entities[0];
                }
                var successPaymentStatus = CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershiptierhistory", "wkcda_paymentstatus", "Success", _logger);
                // Check membership tier history
                var membershipQuery = new QueryExpression("wkcda_membershiptierhistory")
                {
                    ColumnSet = new ColumnSet(
                                "wkcda_membershiptierhistoryid",
                                "wkcda_membershiphistoryname",  // <--- include the name field
                                "wkcda_projectcode",
                                "wkcda_membershipstatus",
                                "wkcda_paymentstatus",
                                "wkcda_startdate",
                                "wkcda_enddate",
                                "wkcda_member"),
                    Criteria = new FilterExpression(LogicalOperator.And)
                };

                membershipQuery.Criteria.AddCondition("wkcda_membershiptier", ConditionOperator.Equal, tierId);
                membershipQuery.Criteria.AddCondition("wkcda_paymentstatus", ConditionOperator.Equal, successPaymentStatus); // Success
                membershipQuery.Criteria.AddCondition("wkcda_startdate", ConditionOperator.OnOrAfter, new DateTime(1990, 1, 2));
                membershipQuery.Criteria.AddCondition("wkcda_member", ConditionOperator.Equal, memberId);

                var membershipHistoryResult = svc.RetrieveMultiple(membershipQuery);
                if (membershipHistoryResult.Entities.Count == 0)
                {
                    return ReturnError($"The member does not have membership for the membership tier: {inputData.MembershipTier}", result);
                }

                foreach (var historyEntity in membershipHistoryResult.Entities)
                {
                    // Retrieve the lookup to contact
                    var memberRef = historyEntity.GetAttributeValue<EntityReference>("wkcda_member");
                    if (memberRef != null)
                    {
                        contact = svc.Retrieve("contact", memberRef.Id,
                            new ColumnSet("wkcda_mastercustomerid", "wkcda_memberid",
                                          "wkcda_wkmemobilephonecountrycode", "wkcda_wkmemobilephonenumber",
                                          "lastname", "firstname", "emailaddress1", "wkcda_status"));
                    }
                    var history = new MembershipHistoryResult
                    {
                        HistoryName = historyEntity.GetAttributeValue<string>("wkcda_membershiphistoryname"),
                        TierName = inputData.MembershipTier,
                        ProjectCode = historyEntity.GetAttributeValue<string>("wkcda_projectcode"),
                        MembershipStatus = historyEntity.GetAttributeValue<string>("wkcda_membershipstatus"),
                        StartDate = historyEntity.GetAttributeValue<DateTime?>("wkcda_startdate"),
                        EndDate = historyEntity.GetAttributeValue<DateTime?>("wkcda_enddate"),

                        MasterCustomerID = contact?.GetAttributeValue<string>("wkcda_mastercustomerid"),
                        MemberID = contact?.GetAttributeValue<string>("wkcda_memberid"),
                        WKmeMobilePhoneCountryCode = contact?.GetAttributeValue<EntityReference>("wkcda_wkmemobilephonecountrycode"),
                        WKmeMobilePhoneNumber = contact?.GetAttributeValue<string>("wkcda_wkmemobilephonenumber"),
                        LastName = contact?.GetAttributeValue<string>("lastname"),
                        FirstName = contact?.GetAttributeValue<string>("firstname"),
                        Email = contact?.GetAttributeValue<string>("emailaddress1"),
                        MemberStatus = CRMEntityHelper.getOptionSetLabel(svc, "contact", "wkcda_status", contact?.GetAttributeValue<OptionSetValue>("wkcda_status")?.Value ?? 0, _logger)
                    };

                    result.MembershipHistory.Add(history);
                }
                return result;
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"unauthorized: {ex.Message}");
                }
                return ReturnError($"Error processing request: {ex.Message}", result);
            }
        }

        private ResponseBody ReturnError(string errorRemarks, ResponseBody result)
        {
            result.Success = false;
            result.Remarks = errorRemarks;
            return result;
        }

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
            public InputDataBean inputData { get; set; }
        }

        public class InputDataBean
        {
            public string MemberID { get; set; }
            public string Email { get; set; }
            public string MembershipTier { get; set; }
        }

        public class ResponseBody
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public List<MembershipHistoryResult> MembershipHistory { get; set; } = new List<MembershipHistoryResult>();
        }
        public class MembershipHistoryResult
        {
            public string HistoryName { get; set; }
            public string TierName { get; set; }
            public string ProjectCode { get; set; }
            public string MembershipStatus { get; set; }
            public DateTime? StartDate { get; set; }
            public DateTime? EndDate { get; set; }
            public string MasterCustomerID { get; set; }
            public string MemberID { get; set; }
            public EntityReference WKmeMobilePhoneCountryCode { get; set; }
            public string WKmeMobilePhoneNumber { get; set; }
            public string LastName { get; set; }
            public string FirstName { get; set; }
            public string Email { get; set; }
            public string MemberStatus { get; set; }
        }
        #endregion
    }
}