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
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using WKCDA_FunctionApp__.Net_8._0_.Model;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using Azure;
using System.Linq;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class SubscriptionWebService : WS_Base
    {
        public SubscriptionWebService(ILogger<SubscriptionWebService> logger) : base(logger) { }

        [Function("SubscriptionWebService")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/SubscriptionWebService")] HttpRequest req)
        {
            _logger.LogInformation("APIName: SubscriptionWebService");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<SubscriptionRequest>();

                if (requestBody?.Customers == null || requestBody.Customers.Count == 0)
                    return new OkObjectResult(new List<UpsertResult> {
                        new UpsertResult { Success = false, Remarks = "Invalid request body or empty customers list" }
                    });

                _logger.LogInformation($"Processing {requestBody.Customers.Count} customer(s)");

                var results = await ProcessCustomerSubscriptions(svc, requestBody.Customers);

                return new OkObjectResult(results);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new List<UpsertResult> {
                    new UpsertResult { Success = false, Remarks = "Invalid JSON format" }
                });
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
                { "Email", "emailaddress1" },
                { "LastName", "lastname" },
                { "FirstName", "firstname" },
                { "Salutation", "salutation" },
                { "District", "address1_city" },
                { "CountryRegion", "address1_country" },
                { "PreferredLanguage", "wkcda_preferredlanguage" },
                { "Interest", "wkcda_interest" },
                { "ArtForm", "wkcda_artform" },
                { "CustomerSource", "wkcda_customersource" },
                { "MobilePhoneNumber", "mobilephone" },
                { "MasterCustomerID", "wkcda_mastercustomerid" },
                { "WK_eNews", "wkcda_westkowloonenewsletter" },
                { "MPlus_eNews", "wkcda_mmembershipenews" },
                { "EmailOptinDate1", "wkcda_emailoptindate1" },
                { "EmailOptinDate2", "wkcda_emailoptindate2" },
                { "OptInChannel1", "wkcda_optinchannel1" },
                { "OptInChannel2", "wkcda_optinchannel2" }
            };
        }

        private async Task<List<UpsertResult>> ProcessCustomerSubscriptions(ServiceClient svc, List<Customer> customers)
        {
            var results = new List<UpsertResult>();
            var mappings = GetMappings();

            foreach (var customer in customers)
            {
                var result = new UpsertResult();

                try
                {
                    if (string.IsNullOrWhiteSpace(customer.Email))
                    {
                        result.Success = false;
                        result.Remarks = "Email is required";
                        results.Add(result);
                        continue;
                    }

                    if (!HasValidSubscriptionData(customer))
                    {
                        result.Success = false;
                        result.Remarks = "error, Subscription data is not provided.";
                        result.MasterCustomerID = null;
                        results.Add(result);
                        continue;
                    }

                    _logger.LogInformation($"Processing customer with email: {customer.Email}");

                    var existingContact = FindContactByEmailEnhanced(svc, customer.Email);

                    if (existingContact != null)
                    {
                        _logger.LogInformation($"Found existing contact for email: {customer.Email}");

                        result = await ProcessExistingContact(svc, existingContact, customer, mappings);
                    }
                    else
                    {
                        _logger.LogInformation($"No existing contact found for email: {customer.Email}, creating new contact");

                        result = CreateContactWithSubscription(svc, customer, mappings);
                    }

                    results.Add(result);
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new Exception($"unauthorized: {ex.Message}");
                    }
                    else
                    {
                        _logger.LogError(ex, $"Error processing customer with email: {customer.Email}");
                        results.Add(new UpsertResult
                        {
                            Success = false,
                            Remarks = $"Failed to process customer: {ex.Message}"
                        });
                    }
                }
            }

            return results;
        }

        private Entity FindContactByEmailEnhanced(ServiceClient svc, string email)
        {
            var query = new QueryExpression("contact")
            {
                ColumnSet = new ColumnSet(
                    "contactid",
                    "emailaddress1",
                    "wkcda_mastercustomerid",
                    "firstname",
                    "lastname",
                    "salutation",
                    "address1_city",
                    "address1_country",
                    "wkcda_preferredlanguage",
                    "wkcda_interest",
                    "wkcda_artform",
                    "wkcda_customersource",
                    "mobilephone",
                    "wkcda_westkowloonenewsletter",
                    "wkcda_mmembershipenews",
                    "wkcda_emailoptindate1",
                    "wkcda_emailoptindate2",
                    "wkcda_optinchannel1",
                    "wkcda_optinchannel2"
                ),
                Criteria = new FilterExpression
                {
                    Conditions =
            {
                new ConditionExpression("emailaddress1", ConditionOperator.Equal, email)
            }
                }/*,
                // Add sorting to handle potential duplicates - take the most recent
                Orders =
        {
            new OrderExpression("createdon", OrderType.Descending)
        }*/
            };

            var result = svc.RetrieveMultiple(query);
            return result.Entities.Count > 0 ? result.Entities[0] : null;
        }

        private async Task<UpsertResult> ProcessExistingContact(ServiceClient svc, Entity existingContact, Customer customer, Dictionary<string, string> mappings)
        {
            var masterCustomerId = existingContact.GetAttributeValue<string>("wkcda_mastercustomerid");

            _logger.LogInformation($"Existing contact found - MasterCustomerID: {masterCustomerId}, " +
                                  $"Name: {existingContact.GetAttributeValue<string>("firstname")} {existingContact.GetAttributeValue<string>("lastname")}");

            var existingSubscriptionCheck = CheckExistingSubscriptions(existingContact, customer, mappings);
            if (existingSubscriptionCheck != null)
            {
                return existingSubscriptionCheck;
            }

            var mergedCustomer = MergeCustomerData(existingContact, customer, mappings);

            return UpdateContactSubscription(svc, existingContact, mergedCustomer, mappings);
        }

        private Customer MergeCustomerData(Entity existingContact, Customer newCustomer, Dictionary<string, string> mappings)
        {
            var mergedCustomer = new Customer
            {
                Email = newCustomer.Email,
                SubscriptionInfos = newCustomer.SubscriptionInfos
            };

            mergedCustomer.LastName = string.IsNullOrWhiteSpace(newCustomer.LastName)
                ? existingContact.GetAttributeValue<string>(mappings["LastName"])
                : newCustomer.LastName;

            mergedCustomer.FirstName = string.IsNullOrWhiteSpace(newCustomer.FirstName)
                ? existingContact.GetAttributeValue<string>(mappings["FirstName"])
                : newCustomer.FirstName;

            mergedCustomer.Salutation = string.IsNullOrWhiteSpace(newCustomer.Salutation)
                ? existingContact.GetAttributeValue<string>(mappings["Salutation"])
                : newCustomer.Salutation;

            mergedCustomer.District = string.IsNullOrWhiteSpace(newCustomer.District)
                ? existingContact.GetAttributeValue<string>(mappings["District"])
                : newCustomer.District;

            mergedCustomer.CountryRegion = string.IsNullOrWhiteSpace(newCustomer.CountryRegion)
                ? existingContact.GetAttributeValue<string>(mappings["CountryRegion"])
                : newCustomer.CountryRegion;

            mergedCustomer.PreferredLanguage = string.IsNullOrWhiteSpace(newCustomer.PreferredLanguage)
                ? GetOptionSetText(existingContact, mappings["PreferredLanguage"])
                : newCustomer.PreferredLanguage;

            mergedCustomer.Interest = string.IsNullOrWhiteSpace(newCustomer.Interest)
                ? GetMultiSelectText(existingContact, mappings["Interest"])
                : newCustomer.Interest;

            mergedCustomer.ArtForm = string.IsNullOrWhiteSpace(newCustomer.ArtForm)
                ? GetMultiSelectText(existingContact, mappings["ArtForm"])
                : newCustomer.ArtForm;

            mergedCustomer.CustomerSource = string.IsNullOrWhiteSpace(newCustomer.CustomerSource)
                ? GetOptionSetText(existingContact, mappings["CustomerSource"])
                : newCustomer.CustomerSource;

            mergedCustomer.MobilePhoneNumber = string.IsNullOrWhiteSpace(newCustomer.MobilePhoneNumber)
                ? existingContact.GetAttributeValue<string>(mappings["MobilePhoneNumber"])
                : newCustomer.MobilePhoneNumber;

            return mergedCustomer;
        }

        private string GetOptionSetText(Entity contact, string attributeName)
        {
            var optionSetValue = contact.GetAttributeValue<OptionSetValue>(attributeName);
            return optionSetValue != null ? optionSetValue.Value.ToString() : null;
        }

        private string GetMultiSelectText(Entity contact, string attributeName)
        {
            var optionSetValues = contact.GetAttributeValue<OptionSetValueCollection>(attributeName);
            if (optionSetValues != null && optionSetValues.Count > 0)
            {
                return string.Join(",", optionSetValues.Select(v => v.Value.ToString()));
            }
            return null;
        }


        private bool HasValidSubscriptionData(Customer customer)
        {
            if (customer.SubscriptionInfos == null || customer.SubscriptionInfos.Count == 0)
                return false;

            foreach (var subscription in customer.SubscriptionInfos)
            {
                if (!string.IsNullOrWhiteSpace(subscription.SubscriptionType))
                {
                    var validSubscriptionTypes = new[] { "wkcda", "mplus" };
                    if (validSubscriptionTypes.Contains(subscription.SubscriptionType.ToLower()))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private UpsertResult CheckExistingSubscriptions(Entity contact, Customer customer, Dictionary<string, string> mappings)
        {
            if (customer.SubscriptionInfos == null || customer.SubscriptionInfos.Count == 0)
                return null;

            var existingSubscriptions = new List<string>();
            var masterCustomerId = contact.GetAttributeValue<string>("wkcda_mastercustomerid");

            bool hasWKSubscription = contact.GetAttributeValue<bool>(mappings["WK_eNews"]);
            bool hasMPlusSubscription = contact.GetAttributeValue<bool>(mappings["MPlus_eNews"]);

            _logger.LogInformation($"Existing subscriptions - WK: {hasWKSubscription}, MPlus: {hasMPlusSubscription}");

            foreach (var subscription in customer.SubscriptionInfos)
            {
                if (string.IsNullOrWhiteSpace(subscription.SubscriptionType))
                    continue;

                switch (subscription.SubscriptionType.ToLower())
                {
                    case "wkcda" when hasWKSubscription:
                        existingSubscriptions.Add("WK_eNews");
                        _logger.LogInformation($"Customer already subscribed to WK_eNews");
                        break;
                    case "mplus" when hasMPlusSubscription:
                        existingSubscriptions.Add("MPlus_eNews");
                        _logger.LogInformation($"Customer already subscribed to MPlus_eNews");
                        break;
                }
            }

            if (existingSubscriptions.Count > 0)
            {
                var subscriptionNames = string.Join(" and ", existingSubscriptions);

                return new UpsertResult
                {
                    Success = false,
                    Remarks = $"{customer.Email} is already subscribed.",
                    MasterCustomerID = masterCustomerId
                };
            }

            return null;
        }

        private Entity FindContactByEmail(ServiceClient svc, string email)
        {
            var query = new QueryExpression("contact")
            {
                ColumnSet = new ColumnSet(
                    "contactid",
                    "emailaddress1",
                    "wkcda_mastercustomerid",
                    "firstname",
                    "lastname",
                    "wkcda_westkowloonenewsletter", 
                    "wkcda_mmembershipenews"
                ),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("emailaddress1", ConditionOperator.Equal, email.ToLower().Trim())
                    }
                }
            };

            var result = svc.RetrieveMultiple(query);
            return result.Entities.Count > 0 ? result.Entities[0] : null;
        }

        private UpsertResult UpdateContactSubscription(ServiceClient svc, Entity contact, Customer customer, Dictionary<string, string> mappings)
        {
            try
            {
                var contactToUpdate = new Entity("contact", contact.Id);

                UpdateContactBasicInfo(svc, contactToUpdate, customer, mappings);

                ProcessSubscriptionInfo(svc, contactToUpdate, customer.SubscriptionInfos, mappings);

                svc.Update(contactToUpdate);

                var masterCustomerId = contact.GetAttributeValue<string>("wkcda_mastercustomerid");

                _logger.LogInformation($"Successfully updated contact: {customer.Email}, MasterCustomerID: {masterCustomerId}");

                return new UpsertResult
                {
                    Success = true,
                    Remarks = customer.Email,
                    MasterCustomerID = masterCustomerId
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating contact for email: {customer.Email}");
                return new UpsertResult
                {
                    Success = false,
                    Remarks = $"Update failed: {ex.Message}"
                };
            }
        }

        private UpsertResult CreateContactWithSubscription(ServiceClient svc, Customer customer, Dictionary<string, string> mappings)
        {
            try
            {
                var newContact = new Entity("contact");

                UpdateContactBasicInfo(svc, newContact, customer, mappings);

                var masterCustomerId = GenerateMasterCustomerID();
                newContact[mappings["MasterCustomerID"]] = masterCustomerId;

                ProcessSubscriptionInfo(svc, newContact, customer.SubscriptionInfos, mappings);

                var contactId = svc.Create(newContact);

                _logger.LogInformation($"Successfully created contact: {customer.Email}, MasterCustomerID: {masterCustomerId}");

                return new UpsertResult
                {
                    Success = true,
                    Remarks = customer.Email,
                    MasterCustomerID = masterCustomerId
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error creating contact for email: {customer.Email}");
                return new UpsertResult
                {
                    Success = false,
                    Remarks = $"Creation failed: {ex.Message}"
                };
            }
        }

        private void UpdateContactBasicInfo(ServiceClient svc, Entity contact, Customer customer, Dictionary<string, string> mappings)
        {
            if (!string.IsNullOrWhiteSpace(customer.Email))
                contact[mappings["Email"]] = customer.Email.ToLower().Trim();
            if (!string.IsNullOrWhiteSpace(customer.LastName))
                contact[mappings["LastName"]] = customer.LastName;
            if (!string.IsNullOrWhiteSpace(customer.FirstName))
                contact[mappings["FirstName"]] = customer.FirstName;
            if (!string.IsNullOrWhiteSpace(customer.Salutation))
                contact[mappings["Salutation"]] = customer.Salutation;
            if (!string.IsNullOrWhiteSpace(customer.District))
                contact[mappings["District"]] = customer.District;
            if (!string.IsNullOrWhiteSpace(customer.CountryRegion))
                contact[mappings["CountryRegion"]] = customer.CountryRegion;

            if (!string.IsNullOrWhiteSpace(customer.PreferredLanguage))
            {
                var preferredLanguageValue = CRMEntityHelper.getOptionSetValue(svc, "contact", "wkcda_preferredlanguage", customer.PreferredLanguage, _logger);
                if (preferredLanguageValue.HasValue)
                    contact[mappings["PreferredLanguage"]] = new OptionSetValue(preferredLanguageValue.Value);
            }

            if (!string.IsNullOrWhiteSpace(customer.Interest))
            {
                if (IsPicklistAttribute(svc, "contact", "wkcda_interest", _logger))
                {
                    var interestValue = CRMEntityHelper.getMutiselectOptionValues(
                        svc, "contact", "wkcda_interest", customer.Interest);

                    if (interestValue != null && interestValue.Any())
                    {
                        _logger.LogInformation("Interest values matched.");
                        contact[mappings["Interest"]] = interestValue;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(customer.ArtForm))
            {
                if (IsPicklistAttribute(svc, "contact", "wkcda_artform", _logger))
                {
                    var interestValue = CRMEntityHelper.getMutiselectOptionValues(
                        svc, "contact", "wkcda_artform", customer.ArtForm);

                    if (interestValue != null && interestValue.Any())
                    {
                        _logger.LogInformation("ArtForm values matched.");
                        contact[mappings["ArtForm"]] = interestValue;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(customer.CustomerSource))
            {
                var customerSourceValue = CRMEntityHelper.getOptionSetValue(svc, "contact", "wkcda_customersource", customer.CustomerSource, _logger);
                if (customerSourceValue.HasValue)
                    contact[mappings["CustomerSource"]] = new OptionSetValue(customerSourceValue.Value);
            }

            if (!string.IsNullOrWhiteSpace(customer.MobilePhoneNumber))
                contact[mappings["MobilePhoneNumber"]] = FormatPhoneNumber(customer.MobilePhoneCountryCode, customer.MobilePhoneNumber);
        }

        private bool IsPicklistAttribute(ServiceClient svc, string entityName, string attributeName, ILogger _logger)
        {
            var request = new RetrieveAttributeRequest
            {
                EntityLogicalName = entityName,
                LogicalName = attributeName,
                RetrieveAsIfPublished = true
            };

            var response = (RetrieveAttributeResponse)svc.Execute(request);
            var metadata = response.AttributeMetadata;

            if (metadata is EnumAttributeMetadata)
            {
                _logger.LogInformation($"Attribute '{attributeName}' is a Picklist.");
                return true;
            }
            else if (metadata is MultiSelectPicklistAttributeMetadata)
            {
                _logger.LogInformation($"Attribute '{attributeName}' is a MultiSelect Picklist.");
                return true;
            }
            else
            {
                _logger.LogWarning($"Attribute '{attributeName}' is NOT a Picklist.");
                return false;
            }
        }

        private void ProcessSubscriptionInfo(ServiceClient svc, Entity contact, List<SubscriptionInfo> subscriptionInfos, Dictionary<string, string> mappings)
        {
            if (subscriptionInfos == null || subscriptionInfos.Count == 0)
                return;

            foreach (var subscription in subscriptionInfos)
            {
                if (string.IsNullOrWhiteSpace(subscription.SubscriptionType))
                    continue;

                switch (subscription.SubscriptionType.ToLower())
                {
                    case "wkcda":
                        contact[mappings["WK_eNews"]] = true;
                        if (!string.IsNullOrWhiteSpace(subscription.optindate) && DateTime.TryParse(subscription.optindate, out DateTime wkcdaDate))
                            contact[mappings["EmailOptinDate1"]] = wkcdaDate;
                        if (!string.IsNullOrWhiteSpace(subscription.optinchannel))
                            contact[mappings["OptInChannel1"]] = GetOptInChannelReference(svc, subscription.optinchannel);
                        break;

                    case "mplus":
                        contact[mappings["MPlus_eNews"]] = true;
                        if (!string.IsNullOrWhiteSpace(subscription.optindate) && DateTime.TryParse(subscription.optindate, out DateTime mplusDate))
                            contact[mappings["EmailOptinDate2"]] = mplusDate;
                        if (!string.IsNullOrWhiteSpace(subscription.optinchannel))
                            contact[mappings["OptInChannel2"]] = GetOptInChannelReference(svc, subscription.optinchannel);
                        break;
                }
            }
        }

        private string GenerateMasterCustomerID()
        {
            var random = new Random();
            int number = random.Next(1, 9999999); 
            return $"P{number:D7}"; 
        }

        private EntityReference GetOptInChannelReference(ServiceClient svc, string channelName)
        {
            if (string.IsNullOrWhiteSpace(channelName))
                return null;

            return CRMEntityHelper.getLookupEntityReference(svc, "wkcda_optinchannel", "wkcda_channelname", channelName);
        }

        private string FormatPhoneNumber(string countryCode, string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
                return null;

            if (!string.IsNullOrWhiteSpace(countryCode))
                return $"{countryCode} {phoneNumber}";

            return phoneNumber;
        }
    }

    #region Request/Response Classes

    public class SubscriptionRequest
    {
        public List<Customer> Customers { get; set; }
    }

    public class Customer
    {
        public string Email { get; set; }
        public string LastName { get; set; }
        public string FirstName { get; set; }
        public string Salutation { get; set; }
        public string District { get; set; }
        public string CountryRegion { get; set; }
        public string PreferredLanguage { get; set; }
        public string Interest { get; set; }
        public string ArtForm { get; set; }
        public string CustomerSource { get; set; }
        public string wk_HomePhoneCountryCode { get; set; }
        public string wk_HomePhoneNumber { get; set; }
        public string wk_OfficePhoneCountryCode { get; set; }
        public string wk_OfficePhoneNumber { get; set; }
        public string MobilePhoneCountryCode { get; set; }
        public string MobilePhoneNumber { get; set; }
        public string InterestStep { get; set; }
        public List<SubscriptionInfo> SubscriptionInfos { get; set; }
    }

    public class SubscriptionInfo
    {
        public string SubscriptionType { get; set; }
        public string optindate { get; set; }
        public string optinchannel { get; set; }
    }

    public class UpsertResult
    {
        public bool Success { get; set; }
        public string Remarks { get; set; }
        public string MasterCustomerID { get; set; }
    }

    #endregion
}