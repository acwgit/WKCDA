using Azure;
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
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class EligibilityCheckUpdate : WS_Base
    {
        public EligibilityCheckUpdate(ILogger<EligibilityCheckUpdate> logger) : base(logger) { }

        [Function("EligibilityCheckUpdate")]
        public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/EligibilityCheckUpdate")] HttpRequest req)
        {
            _logger.LogInformation("APIName: EligibilityCheckUpdate");
            _logger.LogInformation("startTime: " + DateTime.Now);

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();
                var listResults = new List<ResponseBody_Item>();

                foreach (var item in requestBody.eligibilityInfos)
                {
                    var resultItem = new ResponseBody_Item
                    {
                        Success = false,
                        Remarks = "Processing record: " + item.MemberTierHistoryId
                    };

                    try
                    {
                        if (string.IsNullOrEmpty(item.MemberTierHistoryId))
                        {
                            resultItem.Remarks = "MemberTierHistoryId is required.";
                            listResults.Add(resultItem);
                            continue;
                        }

                        // Retrieve Membership Tier History record by Name (wkcda_membershiphistoryname)
                        var query = new QueryExpression("wkcda_membershiptierhistory")
                        {
                            ColumnSet = new ColumnSet("wkcda_needeligibilitycheck", "wkcda_member", "wkcda_membershiphistoryname"),
                            Criteria =
                            {
                                Conditions =
                                {
                                    new ConditionExpression("wkcda_membershiphistoryname", ConditionOperator.Equal, item.MemberTierHistoryId)
                                }
                            }
                        };

                        var results = svc.RetrieveMultiple(query);
                        if (!results.Entities.Any())
                        {
                            resultItem.Remarks = "MembershipTierHistory not found.";
                            listResults.Add(resultItem);
                            continue;
                        }

                        var tierHistory = results.Entities.First();

                        // Update eligibility flag
                        tierHistory["wkcda_needeligibilitycheck"] = item.NeedEligibilityCheck;
                        svc.Update(tierHistory);

                        // retrieve master customer id from member (contact) lookup
                        string masterCustomerId = null;
                        var contactRef = tierHistory.GetAttributeValue<EntityReference>("wkcda_member");
                        if (contactRef != null)
                        {
                            var contact = svc.Retrieve("contact", contactRef.Id,
                                new ColumnSet("wkcda_mastercustomerid")
                            );
                            masterCustomerId = contact.GetAttributeValue<string>("wkcda_mastercustomerid");
                        }

                        resultItem.Success = true;
                        resultItem.Remarks = "Record updated.";
                        resultItem.NeedEligibilityCheck = item.NeedEligibilityCheck;
                        resultItem.MemberTierHistoryId = tierHistory.GetAttributeValue<string>("wkcda_membershiphistoryname");

                        listResults.Add(resultItem);
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new Exception($"unauthorized: {ex.Message}");
                        }
                    }
                }

                return new OkObjectResult(listResults);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new ResponseBody_Item { Remarks = "Invalid JSON", Success = false });
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

        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                // API field              CRM field
                { "MemberTierHistoryId", "wkcda_membershiphistoryname" },
                { "NeedEligibilityCheck", "wkcda_needeligibilitycheck" }
            };
        }

        #region Request & Response Classes
        class RequestBody
        {
            public required List<EligibilityInput> eligibilityInfos { get; set; }
        }

        class EligibilityInput
        {
            public string MemberTierHistoryId { get; set; }
            public bool NeedEligibilityCheck { get; set; }
        }

        class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public bool NeedEligibilityCheck { get; set; }

            /*[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string MasterCustomerID { get; set; }*/
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string MemberTierHistoryId { get; set; }
        }
        #endregion
    }
}
