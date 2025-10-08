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
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class UpdateGroupAccountWS : WS_Base
    {
        public UpdateGroupAccountWS(ILogger<UpdateGroupAccountWS> logger) : base(logger) { }

        [Function("UpdateGroupAccountWS")]
        public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/UpdateGroupAccountWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: UpdateGroupAccountWS");
            _logger.LogInformation("startTime: " + DateTime.Now);

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();
                var listResults = new List<ResponseBody_Item>();

                foreach (var item in requestBody.groupAccountList)
                {
                    var resultItem = new ResponseBody_Item
                    {
                        Success = false,
                        MasterCustomerID = item.MasterCustomerID ?? string.Empty
                    };

                    try
                    {
                        // validation for master customer id or account name
                        if (string.IsNullOrEmpty(item.MasterCustomerID) && string.IsNullOrEmpty(item.AccountName))
                        {
                            resultItem.Remarks = $"{item.AccountName ?? string.Empty}: Please provide MasterCustomerID or AccountName.";
                            listResults.Add(resultItem);
                            continue;
                        }

                        // Retrieve account by master customer id or by Name
                        EntityCollection existingRecords;
                        if (!string.IsNullOrEmpty(item.MasterCustomerID))
                        {
                            var qe = new QueryExpression("account")
                            {
                                ColumnSet = new ColumnSet(true)
                            };
                            qe.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, item.MasterCustomerID);
                            existingRecords = svc.RetrieveMultiple(qe);
                        }
                        else
                        {
                            var qe = new QueryExpression("account")
                            {
                                ColumnSet = new ColumnSet(true)
                            };
                            qe.Criteria.AddCondition("name", ConditionOperator.Equal, item.AccountName);
                            existingRecords = svc.RetrieveMultiple(qe);
                        }

                        if (!existingRecords.Entities.Any())
                        {
                            resultItem.Remarks = "Account not found. Update skipped.";
                            listResults.Add(resultItem);
                            continue;
                        }
                        // not found
                        if (!existingRecords.Entities.Any())
                        {
                            resultItem.Remarks = $"{item.AccountName ?? string.Empty}: Account not found. Update skipped.";
                            listResults.Add(resultItem);
                            continue;
                        }
                        var account = existingRecords.Entities.First();

                        // to update fields
                        account["wkcda_groupaccountnamechi"] = item.AccountNameChi;
                        account["address1_line1"] = item.Address1;
                        account["address1_line2"] = item.Address2;
                        account["address1_line3"] = item.Address3;
                        account["address1_country"] = item.AddressCountry;
                        account["wkcda_billingaddressline1"] = item.BillingAddress1;
                        account["wkcda_billingaddressline2"] = item.BillingAddress2;
                        account["wkcda_billingaddressline3"] = item.BillingAddress3;
                        account["wkcda_billingaddresscountry"] = CRMEntityHelper.getLookupEntityReference(svc, "wkcda_country", "wkcda_countryname", item.BillingAddressCountry);
                        account["wkcda_deliveryaddressline1"] = item.DeliveryAddress1;
                        account["wkcda_deliveryaddressline2"] = item.DeliveryAddress2;
                        account["wkcda_deliveryaddressline3"] = item.DeliveryAddress3;
                        account["wkcda_deliveryaddresscountry"] = CRMEntityHelper.getLookupEntityReference(svc, "wkcda_country", "wkcda_countryname", item.DeliveryAddressCountry);
                        account["wkcda_groupemail"] = item.GroupEmail;
                        account["telephone1"] = item.Phone;
                        account["websiteurl"] = item.Website;
                        account["wkcda_accounttype"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "account", "wkcda_accounttype", item.Type, _logger) ?? 0);
                        account["industrycode"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "account", "industrycode", item.Industry, _logger) ?? 0);
                        account["wkcda_accountsource"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "account", "wkcda_accountsource", item.Source, _logger) ?? 0);
                        account["wkcda_businessregistration"] = item.BusinessRegistration;
                        account["wkcda_media"] = item.Media;
                        account["statecode"] = item.Status == "Active" ? new OptionSetValue(0) : new OptionSetValue(1);
                        account["wkcda_yearstarted"] = item.YearStarted;
                        account["description"] = item.Remarks;
                        account["wkcda_primarycontact"] = item.PrimaryContact;
                        account["emailaddress1"] = item.PrimaryContactEmail;
                        account["wkcda_primarycontactphone"] = item.PrimaryContactPhone;
                        account["wkcda_primarycontactfax"] = item.PrimaryContactFax;
                        account["wkcda_braddressline1"] = item.BRAddress1;
                        account["wkcda_braddressline2"] = item.BRAddress2;
                        account["wkcda_braddressline3"] = item.BRAddress3;
                        account["wkcda_braddresscountry"] = CRMEntityHelper.getLookupEntityReference(svc, "wkcda_country", "wkcda_countryname", item.BRAddressCountry);
                        account["wkcda_customersource"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "account", "wkcda_customersource", item.CustomerSource, _logger) ?? 0);

                        svc.Update(account);

                        resultItem.Success = true;
                        resultItem.MasterCustomerID = account.GetAttributeValue<string>("wkcda_mastercustomerid");
                        resultItem.Remarks = account.Contains("name") ? account.GetAttributeValue<string>("name") : string.Empty;
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
                //API field                       CRM fields
            };
        }

        #region Request & Response Classes
        class RequestBody
        {
            public required List<GroupAccountInput> groupAccountList { get; set; }
        }

        class GroupAccountInput
        {
            public string AccountName { get; set; } // Use name to identify
            public string AccountNameChi { get; set; }
            public string Address1 { get; set; }
            public string Address2 { get; set; }
            public string Address3 { get; set; }
            public string AddressCountry { get; set; }
            public string BillingAddress1 { get; set; }
            public string BillingAddress2 { get; set; }
            public string BillingAddress3 { get; set; }
            public string BillingAddressCountry { get; set; }
            public string DeliveryAddress1 { get; set; }
            public string DeliveryAddress2 { get; set; }
            public string DeliveryAddress3 { get; set; }
            public string DeliveryAddressCountry { get; set; }
            public string GroupEmail { get; set; }
            public string Phone { get; set; }
            public string Website { get; set; }
            public string Type { get; set; }
            public string Industry { get; set; }
            public string Source { get; set; }
            public string BusinessRegistration { get; set; }
            public bool Media { get; set; }
            public string Status { get; set; }
            public string YearStarted { get; set; }
            public string Remarks { get; set; }
            public string MasterCustomerID { get; set; }
            public string PrimaryContact { get; set; }
            public string PrimaryContactEmail { get; set; }
            public string PrimaryContactPhone { get; set; }
            public string PrimaryContactFax { get; set; }
            public string BRAddress1 { get; set; }
            public string BRAddress2 { get; set; }
            public string BRAddress3 { get; set; }
            public string BRAddressCountry { get; set; }
            public string CustomerSource { get; set; }
        }

        class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }

            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string MasterCustomerID { get; set; }
        }
        #endregion
    }
}
