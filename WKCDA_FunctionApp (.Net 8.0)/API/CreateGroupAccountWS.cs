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
using WKCDA_FunctionApp__.Net_8._0_.Helper;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class CreateGroupAccountWS : WS_Base
    {
        public CreateGroupAccountWS(ILogger<CreateGroupAccountWS> logger) : base(logger) { }

        [Function("CreateGroupAccountWS")]
        public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/CreateGroupAccountWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: CreateGroupAccountWS");
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
                        Remarks = item.AccountName
                    };

                    try
                    {
                        if (string.IsNullOrEmpty(item.AccountName))
                        {
                            resultItem.Remarks = "AccountName is required.";
                            listResults.Add(resultItem);
                            continue;
                        }

                        // Check for duplicate Account Name
                        var qe = new QueryExpression("account")
                        {
                            ColumnSet = new ColumnSet("wkcda_mastercustomerid")
                        };
                        qe.Criteria.AddCondition("name", ConditionOperator.Equal, item.AccountName);
                        var existingRecords = svc.RetrieveMultiple(qe);

                        if (existingRecords.Entities.Any())
                        {
                            // Duplicate found, return existing MasterCustomerID
                            var existingAccount = existingRecords.Entities.First();
                            resultItem.Remarks = "Duplicate Account Name. Record not created.";
                            resultItem.MasterCustomerID = existingAccount.GetAttributeValue<string>("wkcda_mastercustomerid");
                            listResults.Add(resultItem);
                            continue;
                        }
                        // create
                        var account = new Entity("account");

                        account["name"] = item.AccountName;
                        account["wkcda_groupaccountnamechi"] = item.AccountNameChi;
                        account["address1_line1"] = item.Address1;
                        account["address1_line2"] = item.Address2;
                        account["address1_line3"] = item.Address3;
                        account["address1_country"] = item.AddressCountry;
                        account["wkcda_billingaddressline1"] = item.BillingAddress1;
                        account["wkcda_billingaddressline2"] = item.BillingAddress2;
                        account["wkcda_billingaddressline3"] = item.BillingAddress3;
                        account["wkcda_billingaddresscountry"] = CRMEntityHelper.getLookupEntityReference(svc, "wkcda_country", "wkcda_countryname", item.BillingAddressCountry); // lookup
                        account["wkcda_deliveryaddressline1"] = item.DeliveryAddress1;
                        account["wkcda_deliveryaddressline2"] = item.DeliveryAddress2;
                        account["wkcda_deliveryaddressline3"] = item.DeliveryAddress3;
                        account["wkcda_deliveryaddresscountry"] = CRMEntityHelper.getLookupEntityReference(svc, "wkcda_country", "wkcda_countryname", item.DeliveryAddressCountry); // lookup
                        account["wkcda_groupemail"] = item.GroupEmail;
                        account["telephone1"] = item.Phone;
                        account["websiteurl"] = item.Website;
                        account["wkcda_accounttype"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "account", "wkcda_accounttype", item.Type, _logger) ?? 0); // option set
                        account["industrycode"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "account", "industrycode", item.Industry, _logger) ?? 0); // option set
                        account["wkcda_accountsource"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "account", "wkcda_accountsource", item.Source, _logger) ?? 0); // option set
                        account["wkcda_businessregistration"] = item.BusinessRegistration;
                        account["wkcda_media"] = item.Media; // bool
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
                        account["wkcda_braddresscountry"] = CRMEntityHelper.getLookupEntityReference(svc, "wkcda_country", "wkcda_countryname", item.BRAddressCountry); // lookup
                        account["wkcda_customersource"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "account", "wkcda_customersource", item.CustomerSource, _logger) ?? 0); // option set

                        account.Id = svc.Create(account);

                        // Retrieve autonumber MasterCustomerID from CRM
                        account = svc.Retrieve("account", account.Id, new ColumnSet("wkcda_mastercustomerid"));
                        string masterCustomerId = account.GetAttributeValue<string>("wkcda_mastercustomerid");

                        resultItem.Success = true;
                        resultItem.MasterCustomerID = masterCustomerId;
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

            };
        }
        #region Request & Response Classes
        class RequestBody
        {
            public required List<GroupAccountInput> groupAccountList { get; set; }
        }

        class GroupAccountInput
        {
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
            public string AccountName { get; set; }
            public string AccountNameChi { get; set; }
            public string Type { get; set; }
            public string Industry { get; set; }
            public string Source { get; set; }
            public string BusinessRegistration { get; set; }
            public bool Media { get; set; }
            public string Status { get; set; }
            public string YearStarted { get; set; }
            public string Remarks { get; set; }
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
