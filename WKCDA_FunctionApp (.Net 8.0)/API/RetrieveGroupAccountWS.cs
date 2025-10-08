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

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class RetrieveGroupAccountWS : WS_Base
    {
        public RetrieveGroupAccountWS(ILogger<RetrieveGroupAccountWS> logger) : base(logger) { }

        [Function("RetrieveGroupAccountWS")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/RetrieveGroupAccountWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: RetrieveGroupAccountWS");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.Customers == null || requestBody.Customers.Count == 0)
                    return new OkObjectResult(new List<ResponseBody> { new ResponseBody { Success = false, Remarks = "Invalid request body" } });

                var resultList = new List<ResponseBody>();
                var validCustomers = new List<CustomerRequest>();

                foreach (var customer in requestBody.Customers)
                {
                    if (string.IsNullOrWhiteSpace(customer.MasterCustomerID) && string.IsNullOrWhiteSpace(customer.AccountName))
                    {
                        resultList.Add(new ResponseBody
                        {
                            Success = false,
                            Remarks = "Please provide MasterCustomerID or AccountName.",
                            Customer = null
                        });
                    }
                    else
                    {
                        validCustomers.Add(customer);
                    }
                }

                if (validCustomers.Count == 0)
                {
                    return new OkObjectResult(resultList);
                }

                var handlerResults = GetGroupCustomers(svc, validCustomers);
                resultList.AddRange(handlerResults);

                return new OkObjectResult(resultList);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new List<ResponseBody> { new ResponseBody { Success = false, Remarks = "Invalid JSON" } });
            }
            catch (Exception ex) when (IsUnauthorizedException(ex))
            {
                _logger.LogError(ex, "Authentication failed");
                return new ObjectResult(new { error = "Unauthorized", message = "Authentication failed" })
                {
                    StatusCode = StatusCodes.Status401Unauthorized
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving group accounts");
                return new ObjectResult(new { error = "Bad Request", message = ex.Message })
                {
                    StatusCode = StatusCodes.Status400BadRequest
                };
            }
        }

        private bool IsUnauthorizedException(Exception ex)
        {
            if (ex == null) return false;

            var message = ex.Message.ToLowerInvariant();
            return message.Contains("unauthorized") ||
                   message.Contains("authorization failed") ||
                   message.Contains("authentication failed") ||
                   ex.InnerException != null && IsUnauthorizedException(ex.InnerException);
        }
        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                { "", "" },
            };
        }

        #region CRM Helpers

        private List<ResponseBody> GetGroupCustomers(ServiceClient svc, List<CustomerRequest> customers)
        {
            var resultList = new List<ResponseBody>();

            try
            {
                foreach (var customer in customers)
                {
                    var result = new ResponseBody();

                    try
                    {
                        QueryExpression query = BuildAccountQuery(customer);
                        var accountResult = svc.RetrieveMultiple(query);

                        if (accountResult.Entities.Count > 0)
                        {
                            var account = accountResult.Entities[0];
                            result.Success = true;
                            result.Customer = MapAccountToCustomer(account);
                            result.Remarks = null;

                            // Validate if essential fields are present
                            if (string.IsNullOrWhiteSpace(result.Customer.MasterCustomerID) &&
                                string.IsNullOrWhiteSpace(result.Customer.AccountName))
                            {
                                result.Success = false;
                                result.Remarks = "Account found but missing essential identification fields (MasterCustomerID and AccountName)";
                                result.Customer = null;
                                _logger.LogWarning($"Account found but missing essential fields for search criteria: MasterCustomerID: {customer.MasterCustomerID}, AccountName: {customer.AccountName}");
                            }
                            else
                            {
                                _logger.LogInformation($"Found account: ID={account.Id}, Name={result.Customer.AccountName}, MasterCustomerID={result.Customer.MasterCustomerID}");
                            }
                        }
                        else
                        {
                            result.Success = false;

                            if (!string.IsNullOrWhiteSpace(customer.MasterCustomerID) && !string.IsNullOrWhiteSpace(customer.AccountName))
                            {
                                result.Remarks = $"No account found with MasterCustomerID: {customer.MasterCustomerID} and AccountName: {customer.AccountName}";
                            }
                            else if (!string.IsNullOrWhiteSpace(customer.MasterCustomerID))
                            {
                                result.Remarks = $"No account found with MasterCustomerID: {customer.MasterCustomerID}";
                            }
                            else if (!string.IsNullOrWhiteSpace(customer.AccountName))
                            {
                                if (!string.IsNullOrWhiteSpace(customer.BusinessRegistration))
                                {
                                    result.Remarks = $"No account found with AccountName: {customer.AccountName} and BusinessRegistration: {customer.BusinessRegistration}";
                                }
                                else
                                {
                                    result.Remarks = $"No account found with AccountName: {customer.AccountName}";
                                }
                            }

                            result.Customer = null;
                            _logger.LogWarning($"No account found for MasterCustomerID: {customer.MasterCustomerID}, AccountName: {customer.AccountName}, BusinessRegistration: {customer.BusinessRegistration}");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new Exception($"unauthorized: {ex.Message}");
                        }
                        _logger.LogError(ex, "Error validating voucher");
                    }

                    resultList.Add(result);
                }
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"unauthorized: {ex.Message}");
                }
                _logger.LogError(ex, "Error validating voucher");
            }

            return resultList;
        }

        private QueryExpression BuildAccountQuery(CustomerRequest customer)
        {
            var query = new QueryExpression("account")
            {
                ColumnSet = new ColumnSet(
                    "name",
                    "wkcda_mastercustomerid",
                    "wkcda_groupaccountnamechi",
                    "address1_line1",
                    "address1_line2",
                    "address1_line3",
                    "address1_country",
                    "address2_line1",
                    "address2_line2",
                    "address2_line3",
                    "address2_country",
                    "wkcda_deliveryaddressline1",
                    "wkcda_deliveryaddressline2",
                    "wkcda_deliveryaddressline3",
                    "wkcda_deliveryaddresscountry",
                    "wkcda_businessregistration",
                    "address2_telephone1",
                    "wkcda_groupemail",
                    "wkcda_primarycontact",
                    "emailaddress1",
                    "wkcda_primarycontactphone",
                    "wkcda_primarycontactfax",
                    "wkcda_media",
                    "wkcda_customersource",
                    "wkcda_accountsource",
                    "wkcda_status",
                    "wkcda_accounttype",
                    "websiteurl",
                    "wkcda_yearstarted",
                    "industrycode",
                    "wkcda_generalremarks"
                )
            };

            var filter = new FilterExpression(LogicalOperator.Or);

            if (!string.IsNullOrWhiteSpace(customer.MasterCustomerID))
            {
                filter.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, customer.MasterCustomerID);
            }

            if (!string.IsNullOrWhiteSpace(customer.AccountName))
            {
                var accountNameFilter = new FilterExpression(LogicalOperator.And);
                accountNameFilter.AddCondition("name", ConditionOperator.Equal, customer.AccountName);

                if (!string.IsNullOrWhiteSpace(customer.BusinessRegistration))
                {
                    accountNameFilter.AddCondition("wkcda_businessregistration", ConditionOperator.Equal, customer.BusinessRegistration);
                }

                filter.AddFilter(accountNameFilter);
            }

            query.Criteria = filter;
            return query;
        }

        private Customer MapAccountToCustomer(Entity account)
        {
            var customer = new Customer
            {
                YearStarted = GetSafeStringValue(account, "wkcda_yearstarted"),
                Website = GetSafeStringValue(account, "websiteurl"),
                Type = GetOptionSetText(account, "wkcda_accounttype"),
                Status = GetOptionSetText(account, "wkcda_status"),
                Source = GetOptionSetText(account, "wkcda_accountsource"),
                Remarks = GetSafeStringValue(account, "wkcda_generalremarks"),
                PrimaryContactPhone = GetSafeStringValue(account, "wkcda_primarycontactphone"),
                PrimaryContactFax = GetSafeStringValue(account, "wkcda_primarycontactfax"),
                PrimaryContactEmail = GetSafeStringValue(account, "emailaddress1"),
                PrimaryContact = GetSafeStringValue(account, "wkcda_primarycontact"),
                Phone = GetSafeStringValue(account, "address2_telephone1"),
                Media = account.GetAttributeValue<bool?>("wkcda_media") ?? false,
                MasterCustomerID = GetSafeStringValue(account, "wkcda_mastercustomerid"),
                Industry = GetOptionSetText(account, "industrycode"),
                GroupEmail = GetSafeStringValue(account, "wkcda_groupemail"),
                DeliveryAddressCountry = GetSafeStringValue(account, "wkcda_deliveryaddresscountry"),
                DeliveryAddress3 = GetSafeStringValue(account, "wkcda_deliveryaddressline3"),
                DeliveryAddress2 = GetSafeStringValue(account, "wkcda_deliveryaddressline2"),
                DeliveryAddress1 = GetSafeStringValue(account, "wkcda_deliveryaddressline1"),
                BusinessRegistration = GetSafeStringValue(account, "wkcda_businessregistration"),
                BillingAddressCountry = GetSafeStringValue(account, "address1_country"),
                BillingAddress3 = GetSafeStringValue(account, "address1_line3"),
                BillingAddress2 = GetSafeStringValue(account, "address1_line2"),
                BillingAddress1 = GetSafeStringValue(account, "address1_line1"),
                AddressCountry = GetSafeStringValue(account, "address2_country"),
                Address3 = GetSafeStringValue(account, "address2_line3"),
                Address2 = GetSafeStringValue(account, "address2_line2"),
                Address1 = GetSafeStringValue(account, "address2_line1"),
                AccountNameChi = GetSafeStringValue(account, "wkcda_groupaccountnamechi"),
                AccountName = GetSafeStringValue(account, "name"),
                CustomerSource = GetOptionSetText(account, "wkcda_customersource")
            };

            customer.MasterCustomerID ??= null;
            customer.AccountName ??= null;

            return customer;
        }

        private string GetSafeStringValue(Entity entity, string attributeName)
        {
            try
            {
                if (!entity.Contains(attributeName))
                    return null;

                var value = entity[attributeName];

                if (value is EntityReference entityRef)
                    return entityRef.Name; 
                else if (value is AliasedValue aliasedValue)
                    return aliasedValue.Value?.ToString();
                else
                    return value?.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error getting value for attribute {attributeName}: {ex.Message}");
                return null;
            }
        }

        private string GetOptionSetText(Entity entity, string attributeName, string defaultText = null)
        {
            try
            {
                if (!entity.Contains(attributeName))
                    return defaultText;

                var optionSetValue = entity.GetAttributeValue<OptionSetValue>(attributeName);
                if (optionSetValue != null)
                {
                    return entity.FormattedValues.ContainsKey(attributeName)
                        ? entity.FormattedValues[attributeName]
                        : optionSetValue.Value.ToString();
                }
                return defaultText;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Error getting option set text for {attributeName}: {ex.Message}");
                return defaultText;
            }
        }

        #endregion

        #region Request/Response Classes

        public class RequestBody
        {
            public List<CustomerRequest> Customers { get; set; }
        }

        public class CustomerRequest
        {
            public string MasterCustomerID { get; set; }
            public string AccountName { get; set; }
            public string BusinessRegistration { get; set; }
        }

        public class ResponseBody
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public Customer Customer { get; set; }
        }

        public class Customer
        {
            public string YearStarted { get; set; }
            public string Website { get; set; }
            public string Type { get; set; }
            public string Status { get; set; }
            public string Source { get; set; }
            public string Remarks { get; set; }
            public string PrimaryContactPhone { get; set; }
            public string PrimaryContactFax { get; set; }
            public string PrimaryContactEmail { get; set; }
            public string PrimaryContact { get; set; }
            public string Phone { get; set; }
            public bool Media { get; set; }
            public string MasterCustomerID { get; set; }
            public string Industry { get; set; }
            public string GroupEmail { get; set; }
            public string DeliveryAddressCountry { get; set; }
            public string DeliveryAddress3 { get; set; }
            public string DeliveryAddress2 { get; set; }
            public string DeliveryAddress1 { get; set; }
            public string BusinessRegistration { get; set; }
            public string BillingAddressCountry { get; set; }
            public string BillingAddress3 { get; set; }
            public string BillingAddress2 { get; set; }
            public string BillingAddress1 { get; set; }
            public string AddressCountry { get; set; }
            public string Address3 { get; set; }
            public string Address2 { get; set; }
            public string Address1 { get; set; }
            public string AccountNameChi { get; set; }
            public string AccountName { get; set; }
            public string CustomerSource { get; set; }
        }

        #endregion
    }
}