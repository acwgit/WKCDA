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
    public class RetrieveExternalSystemReferenceWS : WS_Base
    {
        public RetrieveExternalSystemReferenceWS(ILogger<RetrieveExternalSystemReferenceWS> logger) : base(logger)
        {
        }
        [Function("RetrieveExternalSystemReferenceWS")]
        public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/RetrieveExternalSystemReferenceWS")] HttpRequest req)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");
            //return new OkObjectResult($"OK");
            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                // var ec = GetExternalSystemReferences(svc, requestBody);

                var mappings = GetMappings();
                var listResults = new List<ResponseBody_Item>();
                foreach (var reqCustomer in requestBody.exterReferList)
                {
                    // Initialize ExternalSysRefer with incoming keys
                    var mapped = new ExternalSysRefer
                    {
                        SystemSourceKey = reqCustomer.SystemSourceKey,
                        MasterCustomerID = reqCustomer.MasterCustomerID
                    };
                    // SystemSourceKey required
                    if (string.IsNullOrWhiteSpace(reqCustomer.SystemSourceKey))
                    {
                        listResults.Add(new ResponseBody_Item
                        {
                            Success = false,
                            Remarks = "Please provide SystemSourceKey.",
                            ExternalSysRefer = mapped
                        });
                        return new OkObjectResult(listResults);
                    }

                    // MasterCustomerID required
                    if (string.IsNullOrWhiteSpace(reqCustomer.MasterCustomerID))
                    {
                        listResults.Add(new ResponseBody_Item
                        {
                            Success = false,
                            Remarks = "Please provide MasterCustomerID.",
                            ExternalSysRefer = mapped
                        });
                        return new OkObjectResult(listResults);
                    }

                    var contact = RetrieveContactByMasterCustomerID(svc, reqCustomer.MasterCustomerID);
                    if (contact == null)
                    {
                        listResults.Add(new ResponseBody_Item
                        {
                            Success = false,
                            Remarks = "Record does not exist.",
                            ExternalSysRefer = mapped
                        });
                        continue;
                    }

                    var extSysRef = RetrieveExternalSystemReferenceByContact(svc, reqCustomer.SystemSourceKey, contact.Id);
                    if (extSysRef == null)
                    {
                        listResults.Add(new ResponseBody_Item
                        {
                            Success = false,
                            Remarks = "Record does not exist.",
                            ExternalSysRefer = mapped
                        });
                        continue;
                    }

                    foreach (var map in GetMappings())
                    {
                        var apiField = map.Key;
                        var crmField = map.Value;
                        var value = extSysRef.GetAttributeValue<string>(crmField);
                        typeof(ExternalSysRefer).GetProperty(apiField)?.SetValue(mapped, value);
                    }

                    var memberLookup = extSysRef.GetAttributeValue<EntityReference>("wkcda_member");
                    if (memberLookup != null)
                    {
                        var memberContact = svc.Retrieve("contact", memberLookup.Id, new ColumnSet("wkcda_mastercustomerid"));
                        mapped.MasterCustomerID = memberContact?.GetAttributeValue<string>("wkcda_mastercustomerid") ?? string.Empty;
                    }

                    listResults.Add(new ResponseBody_Item
                    {
                        Success = true,
                        Remarks = null,
                        ExternalSysRefer = mapped
                    });
                }
                return new OkObjectResult(listResults);
            }
            catch (JsonException ex)
            {
                var response = new ResponseBody_Item
                {
                    Remarks = "Invalid Json",
                    Success = false,
                };
                return new OkObjectResult(response);
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
        /// <summary>
        /// Key     = Response field
        /// Value   = CRM field
        /// </summary>
        /// <returns></returns>
        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                //API field                  CRM field
                { "SystemSourceKey"        , "wkcda_systemsourcekey" },
                { "BillingAddress1"        , "wkcda_billingaddress1" },
                { "BillingAddress2"        , "wkcda_billingaddress2" },
                { "BillingAddress3"        , "wkcda_billingaddress3" },
                { "BillingAddressCountry"  , "wkcda_billingaddresscountry" },
                { "BillingContactName"     , "wkcda_billingcontactname" },
                { "BillingContactEmail"    , "wkcda_billingcontactemail" },
                { "BillingContactPhone"    , "wkcda_billingcontactphone" },
                { "BillingContactFax"      , "wkcda_billingcontactfax" },
                { "ExternalReferenceName"  , "wkcda_externalsystemreferencename" }
            };
        }
        private const string CrmEntityToSearch = "wkcda_externalsystemreference";
        private const string CrmFieldToSearchSystemKey = "wkcda_systemsourcekey";

        private ColumnSet GetColumnSetFromMapping(string? entityAlias = null)
        {
            var mappings = GetMappings();
            var crmFields = mappings.Values
                                    .Select(o => o.ToLower().Trim())
                                    .Where(o => !string.IsNullOrEmpty(o))
                                    .Where(o => entityAlias != null ? o.StartsWith($"{entityAlias}.") : !o.Contains("."))
                                    .Select(o => entityAlias != null ? o.Substring(($"{entityAlias}.").Length) : o)
                                    .ToArray();

            return new ColumnSet(crmFields);
        }
        /// <summary>
        /// Retrieves contact by MasterCustomerID
        /// </summary>
        private Entity RetrieveContactByMasterCustomerID(ServiceClient svc, string masterCustomerID)
        {
            var qe = new QueryExpression("contact")
            {
                ColumnSet = new ColumnSet("contactid", "wkcda_mastercustomerid")
            };
            qe.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, masterCustomerID);
            return svc.RetrieveMultiple(qe).Entities.FirstOrDefault();
        }

        private Entity RetrieveExternalSystemReferenceByContact(ServiceClient svc, string systemSourceKey, Guid contactId)
        {
            var qe = new QueryExpression("wkcda_externalsystemreference")
            {
                ColumnSet = new ColumnSet(
                    "wkcda_systemsourcekey",
                    "wkcda_billingaddress1",
                    "wkcda_billingaddress2",
                    "wkcda_billingaddress3",
                    "wkcda_billingaddresscountry",
                    "wkcda_billingcontactname",
                    "wkcda_billingcontactemail",
                    "wkcda_billingcontactphone",
                    "wkcda_billingcontactfax",
                    "wkcda_member"
                )
            };
            qe.Criteria.AddCondition("wkcda_systemsourcekey", ConditionOperator.Equal, systemSourceKey);
            qe.Criteria.AddCondition("wkcda_member", ConditionOperator.Equal, contactId);
            return svc.RetrieveMultiple(qe).Entities.FirstOrDefault();
        }
        #region Request Class
        class RequestBody
        {
            public required ExterRefer[] exterReferList { get; set; }
        }

        class ExterRefer
        {
            public required string SystemSourceKey { get; set; }
            public required string MasterCustomerID { get; set; }
        }
        #endregion

        #region Response Class
        class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string? Remarks { get; set; }
            public ExternalSysRefer? ExternalSysRefer { get; set; }


        }
        #endregion
        #region ExterSysRef
        class ExternalSysRefer
        {
            public string? SystemSourceKey { get; set; }
            public string? MasterCustomerID { get; set; }
            public string? BillingAddress1 { get; set; }
            public string? BillingAddress2 { get; set; }
            public string? BillingAddress3 { get; set; }
            public string? BillingAddressCountry { get; set; }
            public string? BillingContactName { get; set; }
            public string? BillingContactEmail { get; set; }
            public string? BillingContactPhone { get; set; }
            public string? BillingContactFax { get; set; }
        }
        #endregion
    }
}