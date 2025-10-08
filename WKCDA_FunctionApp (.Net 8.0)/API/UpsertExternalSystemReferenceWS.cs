using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Authentication;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class UpsertExternalSystemReferenceWS : WS_Base
    {
        public UpsertExternalSystemReferenceWS(ILogger<UpsertExternalSystemReferenceWS> logger) : base(logger) { }

        [Function("UpsertExternalSystemReferenceWS")]
        public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/UpsertExternalSystemReferenceWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: UpsertExternalSystemReferenceWS");
            _logger.LogInformation("startTime: " + DateTime.Now);

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                var listResults = new List<ResponseBody_Item>();

                for (int i = 0; i < requestBody.externalSysReferList.Count; i++)
                {
                    var item = requestBody.externalSysReferList[i];
                    var resultItem = new ResponseBody_Item
                    {
                        SystemSourceKey = item.SystemSourceKey,
                        Success = false
                    };

                    try
                    {
                        // validate system source key
                        if (string.IsNullOrEmpty(item.SystemSourceKey))
                        {
                            resultItem.Remarks = "Please provide SystemSourceKey.";
                            resultItem.MasterCustomerID = item.MasterCustomerID;
                            listResults.Add(resultItem);
                            continue;
                        }

                        // validate master customer ID
                        if (string.IsNullOrEmpty(item.MasterCustomerID))
                        {
                            resultItem.Remarks = "Please provide MasterCustomerID.";
                            resultItem.MasterCustomerID = null;
                            listResults.Add(resultItem);
                            continue;
                        }

                        // lookup member to contact
                        var memberQuery = new QueryExpression("contact")
                        {
                            ColumnSet = new ColumnSet("contactid", "fullname", "emailaddress1")
                        };
                        memberQuery.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, item.MasterCustomerID);
                        var memberResult = svc.RetrieveMultiple(memberQuery);

                        if (!memberResult.Entities.Any())
                        {
                            resultItem.Remarks = $"MasterCustomerID {item.MasterCustomerID} not found in wkcda_member.";
                            listResults.Add(resultItem);
                            continue;
                        }

                        var member = memberResult.Entities.First();
                        var memberId = member.Id;

                        var billingName = member.GetAttributeValue<string>("fullname");
                        var billingEmail = member.GetAttributeValue<string>("emailaddress1");

                        var qe = new QueryExpression("wkcda_externalsystemreference")
                        {
                            ColumnSet = new ColumnSet("wkcda_externalsystemreferenceid")
                        };
                        qe.Criteria.AddCondition("wkcda_member", ConditionOperator.Equal, memberId);
                        var existingRecords = svc.RetrieveMultiple(qe);

                        Entity externalRef;
                        if (existingRecords.Entities.Any())
                        {
                            externalRef = existingRecords.Entities.First();
                        }
                        else
                        {
                            externalRef = new Entity("wkcda_externalsystemreference");
                        }

                        externalRef["wkcda_member"] = new EntityReference("contact", memberId);
                        externalRef["wkcda_systemsourcekey"] = item.SystemSourceKey;
                        externalRef["wkcda_billingcontactname"] = billingName;
                        externalRef["wkcda_billingcontactemail"] = billingEmail;
                        externalRef["wkcda_billingaddress1"] = item.BillingAddress1;
                        externalRef["wkcda_billingaddress2"] = item.BillingAddress2;
                        externalRef["wkcda_billingaddress3"] = item.BillingAddress3;
                        externalRef["wkcda_billingaddresscountry"] = item.BillingAddressCountry;
                        externalRef["wkcda_billingcontactphone"] = item.BillingContactPhone;
                        externalRef["wkcda_billingcontactfax"] = item.BillingContactFax;

                        if (externalRef.Id != Guid.Empty)
                            svc.Update(externalRef);
                        else
                            externalRef.Id = svc.Create(externalRef);


                        resultItem.Success = true;
                        resultItem.MasterCustomerID = item.MasterCustomerID;
                        listResults.Add(resultItem);
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                        {
                            return new ObjectResult(new { error = "Unauthorized", message = ex.Message })
                            { StatusCode = StatusCodes.Status401Unauthorized };
                        }
                        return new ObjectResult(new { error = "Bad Request", message = ex.Message })
                        { StatusCode = StatusCodes.Status400BadRequest };
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
        /// <summary>
        /// Key     = Response field
        /// Value   = CRM field
        /// </summary>
        /// <returns></returns>
        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                //API field                       CRM fields
                { "BillingContactName",         "firstname" },
                { "BillingContactEmail",        "emailaddress1" },
                { "BillingAddress1",            "address1_line1" },
                { "BillingAddress2",            "address1_line2" },
                { "BillingAddress3",            "address1_line3" },
                { "BillingAddressCountry",      "address1_country" },
                { "BillingContactPhone",        "telephone1" },
                { "BillingContactFax",          "fax" }
            };
        }
        #region Request & Response Classes
        class RequestBody
        {
            public required List<ExternalCustomerInput> externalSysReferList { get; set; }
        }

        class ExternalCustomerInput
        {
            public string SystemSourceKey { get; set; }
            public string MasterCustomerID { get; set; }
            public string BillingContactName { get; set; }
            public string BillingContactEmail { get; set; }
            public string BillingAddress1 { get; set; }
            public string BillingAddress2 { get; set; }
            public string BillingAddress3 { get; set; }
            public string BillingAddressCountry { get; set; }
            public string BillingContactPhone { get; set; }
            public string BillingContactFax { get; set; }
        }

        class ResponseBody_Item
        {
            public string SystemSourceKey { get; set; }
            public bool Success { get; set; }
            public string Remarks { get; set; }

            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string MasterCustomerID { get; set; }
        }
        #endregion
    }
}
