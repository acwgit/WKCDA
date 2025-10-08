using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class GiftPurchase : WS_Base
    {
        public GiftPurchase(ILogger<GiftPurchase> logger) : base(logger) { }

        [Function("GiftPurchase")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/GiftPurchase")] HttpRequest req)
        {
            _logger.LogInformation("APIName: GiftPurchaseWS");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.customerEntity?.AccountProfile == null || requestBody.customerEntity.AccountProfile.Count == 0)
                    return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Invalid request body" } });

                if (requestBody.customerEntity.Login.Equals(false))
                {
                    return new OkObjectResult(new List<ResponseBody_Item>
                    {
                        new ResponseBody_Item { Success = false, Remarks = "Please log in your MyWestKowloon Account to continue." }
                    });
                }

                ConvertMembershipDetails(requestBody.customerEntity);

                var response = await ProcessGiftPurchase(svc, requestBody.customerEntity);

                return new OkObjectResult(response);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "JSON deserialization error");
                return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Invalid JSON format" } });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GiftPurchase API");
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

        private async Task<List<ResponseBody_Item>> ProcessGiftPurchase(ServiceClient svc, CustomerEntity customerEntity)
        {
            var response = new List<ResponseBody_Item>();

            foreach (var account in customerEntity.AccountProfile)
            {
                try
                {
                    _logger.LogInformation($"Processing account with email: {account.Email}");

                    string decryptedEmail = DecodeBase64Field(account.Email);
                    string decryptedMobileNumber = DecodeBase64Field(account.MobilePhoneNumber);

                    _logger.LogInformation($"Decrypted email: {decryptedEmail}");
                    _logger.LogInformation($"Decrypted phone: {decryptedMobileNumber}");

                    if (string.IsNullOrWhiteSpace(decryptedEmail))
                    {
                        response.Add(new ResponseBody_Item { Success = false, Remarks = "Email is required" });
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(account.MembershipTier))
                    {
                        response.Add(new ResponseBody_Item { Success = false, Remarks = "Membership tier is required" });
                        continue;
                    }

                    account.Email = decryptedEmail;
                    account.MobilePhoneNumber = decryptedMobileNumber;

                    var contactId = await HandleContact(svc, account);
                    _logger.LogInformation($"Contact processed with ID: {contactId}");

                    var paymentHistoryId = CreatePaymentTransaction(svc, contactId, account);
                    _logger.LogInformation($"Payment transaction created with ID: {paymentHistoryId}");

                    var memberTierHistoryId = CreateMembershipTierHistory(svc, contactId, account);
                    _logger.LogInformation($"Membership tier history created with ID: {memberTierHistoryId}");

                    var groupRelationshipId = CreateGroupRelationship(svc, contactId, account);
                    _logger.LogInformation($"Group relationship created with ID: {groupRelationshipId}");

                    var activationCode = GenerateActivationCode();
                    var activationId = CreateMembershipActivation(svc, contactId, activationCode, account);
                    _logger.LogInformation($"Membership activation created with ID: {activationId} and code: {activationCode}");

                    if (account.SubscriptionInfos != null && account.SubscriptionInfos.Any())
                    {
                        UpdateSubscriptions(svc, contactId, account.SubscriptionInfos);
                        _logger.LogInformation($"Subscriptions updated for contact: {contactId}");
                    }

                    response.Add(new ResponseBody_Item
                    {
                        Success = true,
                        Remarks = "Gift purchase processed successfully",
                        Reference = account.ReferenceNo,
                        PaymentHistoryId = paymentHistoryId.ToString(),
                        MemberTierHistoryId = memberTierHistoryId.ToString(),
                        MasterCustomerID = contactId.ToString(),
                        GroupRelationshipId = groupRelationshipId?.ToString() ?? string.Empty,
                        ActivationCode = activationCode
                    });

                    _logger.LogInformation($"Successfully processed gift purchase for email: {decryptedEmail}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error processing account with email: {account.Email}");

                    if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new Exception($"unauthorized: {ex.Message}");
                    }

                    string errorMessage = ex.Message;

                    if (ex.Message.Contains("contact", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
                        {
                            errorMessage = "A contact with this email already exists in the system";
                        }
                        else if (ex.Message.Contains("create", StringComparison.OrdinalIgnoreCase))
                        {
                            errorMessage = "Failed to create contact record";
                        }
                        else if (ex.Message.Contains("update", StringComparison.OrdinalIgnoreCase))
                        {
                            errorMessage = "Failed to update contact record";
                        }
                    }

                    response.Add(new ResponseBody_Item
                    {
                        Success = false,
                        Remarks = $"Error processing gift purchase: {errorMessage}"
                    });
                }
            }

            return response;
        }
        private async Task<Guid> HandleContact(ServiceClient svc, AccountProfile account)
        {
            var existingContact = await FindExistingContact(svc, account.Email);

            if (existingContact != null)
            {
                _logger.LogInformation($"Found existing contact via email search: {existingContact.Id}");
                UpdateContactFields(svc, existingContact, account);
                return existingContact.Id;
            }

            _logger.LogInformation("Email search failed, trying multi-criteria search");
            var multiCriteriaContact = FindContactByMultipleCriteria(svc, account);

            if (multiCriteriaContact != null)
            {
                _logger.LogInformation($"Found existing contact via multi-criteria search: {multiCriteriaContact.Id}");
                UpdateContactFields(svc, multiCriteriaContact, account);
                return multiCriteriaContact.Id;
            }

            _logger.LogInformation("No existing contact found, creating new contact");
            return CreateNewContact(svc, account);
        }
        private void ConvertMembershipDetails(CustomerEntity customerEntity)
        {
            if (customerEntity?.AccountProfile == null || !customerEntity.AccountProfile.Any())
                return;

            var primaryAccount = customerEntity.AccountProfile[0];

            var newAccountProfiles = new List<AccountProfile>();

            if (primaryAccount?.MembershipDetails != null && primaryAccount.MembershipDetails.Any())
            {
                int i = 0;
                foreach (var membershipDetail in primaryAccount.MembershipDetails)
                {
                    i++;

                    var accountProfile = new AccountProfile
                    {
                        Email = primaryAccount.Email,
                        FirstName = primaryAccount.FirstName,
                        LastName = primaryAccount.LastName,
                        MobilePhoneNumber = primaryAccount.MobilePhoneNumber,
                        MembershipTier = membershipDetail.MembershipTier,
                        ReferenceNo = membershipDetail.ReferenceNo,
                        IsChild = membershipDetail.IsChild,
                        IsStudent = membershipDetail.IsStudent,
                        TotalAmount = membershipDetail.TotalAmount?.ToString(),
                        PaymentAmount = membershipDetail.PaymentAmount?.ToString(),
                        DiscountAmount = membershipDetail.DiscountAmount?.ToString(),
                        PaymentType = membershipDetail.PaymentType,
                        SalesChannel = membershipDetail.SalesChannel,
                        PaymemtDate = membershipDetail.PaymentDate?.ToString("yyyy-MM-dd"),
                        PaidBy = membershipDetail.PaidBy,
                        MemberGroupType = membershipDetail.MemberGroupType,
                        MemberGroupRole = membershipDetail.MemberGroupRole,
                        SubscriptionInfos = membershipDetail.SubscriptionInfos,
                        SrNo = i.ToString(),
                        MembershipDetails = new List<MembershipDetail> { membershipDetail }
                    };

                    newAccountProfiles.Add(accountProfile);
                }
            }
            else
            {
                newAccountProfiles.Add(primaryAccount);
            }

            customerEntity.AccountProfile = newAccountProfiles;
        }

        private string DecodeBase64Field(string encodedValue)
        {
            if (string.IsNullOrWhiteSpace(encodedValue))
                return string.Empty;

            if (IsNotBase64(encodedValue))
            {
                _logger.LogInformation($"not Base64, returning as-is: {encodedValue}");
                return encodedValue;
            }

            try
            {
                if (IsBase64String(encodedValue))
                {
                    byte[] data = Convert.FromBase64String(encodedValue);
                    string decodedString = Encoding.UTF8.GetString(data);

                    //  _logger.LogInformation($"Successfully decoded Base64: {encodedValue} -> {decodedString}");
                    return decodedString;
                }
                else
                {
                    //  _logger.LogInformation($"Not a Base64 string, returning as-is: {encodedValue}");
                    return encodedValue;
                }
            }
            catch (Exception ex)
            {
                //  _logger.LogError(ex, $"Error decoding Base64 field value: {encodedValue}");
                return encodedValue;
            }
        }

        private bool IsNotBase64(string value)
        {
            if (value.All(char.IsDigit))
                return true;

            if (value.Length < 4 && !value.Contains('/') && !value.Contains('+') && !value.Contains('='))
                return true;

            return false;
        }

        private bool IsBase64String(string base64)
        {
            if (string.IsNullOrEmpty(base64) || base64.Length % 4 != 0)
                return false;

            // Base64 strings should contain mostly Base64 characters
            string base64Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=";
            int base64CharCount = base64.Count(c => base64Chars.Contains(c));

            // If less than 90% of characters are valid Base64 chars, it's probably not Base64
            if ((double)base64CharCount / base64.Length < 0.9)
                return false;

            try
            {
                byte[] data = Convert.FromBase64String(base64);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsValidPhoneNumber(string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
                return false;

            // Check if every character is a digit
            return phoneNumber.All(char.IsDigit);
        }

        #region CRM Helper Methods

        private async Task<EntityReference> FindExistingContact(ServiceClient svc, string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return null;

            try
            {
                _logger.LogInformation($"Searching for existing contact with email: {email}");

                var contactRef = GetContactReference(svc, email);

                if (contactRef != null)
                {
                    _logger.LogInformation($"Found contact via email search: {contactRef.Id}");
                    return contactRef;
                }

                _logger.LogInformation($"No contact found with email: {email}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error finding existing contact for email: {email}");
                return null;
            }
        }

        private async Task<EntityReference> FindContactByAdditionalCriteria(ServiceClient svc, string email)
        {

            return await Task.FromResult<EntityReference>(null);
        }

        private EntityReference GetContactReference(ServiceClient svc, string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return null;

            try
            {
                var query = new QueryExpression("contact")
                {
                    ColumnSet = new ColumnSet("contactid", "emailaddress1", "firstname", "lastname", "mobilephone"),
                    TopCount = 10 // Increased to see if we're getting multiple matches
                };

                // Add email condition - try multiple approaches
                var emailFilter = query.Criteria.AddFilter(LogicalOperator.Or);

                // Exact match
                emailFilter.AddCondition("emailaddress1", ConditionOperator.Equal, email.Trim().ToLower());

                // Case insensitive contains (in case there are formatting differences)
                emailFilter.AddCondition("emailaddress1", ConditionOperator.Equal, email.Trim());

                // Also check if email might be in a different field
                emailFilter.AddCondition("emailaddress2", ConditionOperator.Equal, email.Trim().ToLower());
                emailFilter.AddCondition("emailaddress3", ConditionOperator.Equal, email.Trim().ToLower());

                var result = svc.RetrieveMultiple(query);

                _logger.LogInformation($"Contact search found {result.Entities.Count} matches for email: {email}");

                if (result.Entities.Count > 0)
                {
                    foreach (var entity in result.Entities)
                    {
                        _logger.LogInformation($"Contact ID: {entity.Id}, Email: {entity.GetAttributeValue<string>("emailaddress1")}");
                    }
                    return result.Entities[0].ToEntityReference();
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retrieving contact reference for email: {email}");
                return null;
            }
        }
        private EntityReference FindContactByMultipleCriteria(ServiceClient svc, AccountProfile account)
        {
            if (string.IsNullOrWhiteSpace(account.Email) && string.IsNullOrWhiteSpace(account.MobilePhoneNumber))
                return null;

            try
            {
                var query = new QueryExpression("contact")
                {
                    ColumnSet = new ColumnSet("contactid", "emailaddress1", "firstname", "lastname", "mobilephone"),
                    TopCount = 10
                };

                var mainFilter = query.Criteria.AddFilter(LogicalOperator.Or);

                if (!string.IsNullOrWhiteSpace(account.Email))
                {
                    // Email matches
                    var emailFilter = mainFilter.AddFilter(LogicalOperator.Or);
                    emailFilter.AddCondition("emailaddress1", ConditionOperator.Equal, account.Email.Trim().ToLower());
                    emailFilter.AddCondition("emailaddress2", ConditionOperator.Equal, account.Email.Trim().ToLower());
                    emailFilter.AddCondition("emailaddress3", ConditionOperator.Equal, account.Email.Trim().ToLower());
                }

                if (!string.IsNullOrWhiteSpace(account.MobilePhoneNumber))
                {
                    // Mobile phone matches (clean the number first)
                    var cleanMobile = CleanPhoneNumber(account.MobilePhoneNumber);
                    if (!string.IsNullOrWhiteSpace(cleanMobile))
                    {
                        var phoneFilter = mainFilter.AddFilter(LogicalOperator.Or);
                        phoneFilter.AddCondition("mobilephone", ConditionOperator.Equal, cleanMobile);
                        phoneFilter.AddCondition("telephone1", ConditionOperator.Equal, cleanMobile);
                    }
                }

                var result = svc.RetrieveMultiple(query);

                _logger.LogInformation($"Multi-criteria search found {result.Entities.Count} matches");

                if (result.Entities.Count > 0)
                {
                    // Return the first match
                    return result.Entities[0].ToEntityReference();
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in multi-criteria contact search");
                return null;
            }
        }

        private string CleanPhoneNumber(string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
                return string.Empty;

            // Remove all non-digit characters
            return new string(phoneNumber.Where(char.IsDigit).ToArray());
        }

        private void UpdateContactFields(ServiceClient svc, EntityReference contactRef, AccountProfile account)
        {
            try
            {
                var contactEntity = new Entity("contact", contactRef.Id);

                bool hasUpdates = false;

                if (!string.IsNullOrWhiteSpace(account.FirstName))
                {
                    contactEntity["firstname"] = account.FirstName;
                    hasUpdates = true;
                }

                if (!string.IsNullOrWhiteSpace(account.LastName))
                {
                    contactEntity["lastname"] = account.LastName;
                    hasUpdates = true;
                }

                if (!string.IsNullOrWhiteSpace(account.Email))
                {
                    contactEntity["emailaddress1"] = account.Email;
                    hasUpdates = true;
                }

                if (!string.IsNullOrWhiteSpace(account.MobilePhoneNumber))
                {
                    contactEntity["mobilephone"] = account.MobilePhoneNumber;
                    hasUpdates = true;
                }

                if (hasUpdates)
                {
                    svc.Update(contactEntity);
                    _logger.LogInformation($"Successfully updated contact {contactRef.Id}");
                }
                else
                {
                    _logger.LogInformation($"No updates needed for contact {contactRef.Id}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating contact fields for contact ID: {contactRef.Id}");
                throw;
            }
        }

        private Guid CreateNewContact(ServiceClient svc, AccountProfile account)
        {
            try
            {
                var contactEntity = new Entity("contact");

                if (string.IsNullOrWhiteSpace(account.Email))
                    throw new ArgumentException("Email is required for new contact");

                contactEntity["emailaddress1"] = account.Email;

                if (!string.IsNullOrWhiteSpace(account.FirstName))
                    contactEntity["firstname"] = account.FirstName;

                if (!string.IsNullOrWhiteSpace(account.LastName))
                    contactEntity["lastname"] = account.LastName;

                if (!string.IsNullOrWhiteSpace(account.MobilePhoneNumber))
                    contactEntity["mobilephone"] = account.MobilePhoneNumber;

                contactEntity["wkcda_mastercustomerid"] = $"P{DateTime.UtcNow.Ticks.ToString().Substring(0, 10)}";

                var contactId = svc.Create(contactEntity);
                _logger.LogInformation($"Successfully created new contact with ID: {contactId}");

                return contactId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating new contact");
                throw;
            }
        }

        private Guid CreatePaymentTransaction(ServiceClient svc, Guid contactId, AccountProfile account)
        {
            try
            {
                var entity = new Entity("wkcda_paymenttransaction");

                if (decimal.TryParse(account.TotalAmount, out decimal totalAmount))
                    entity["wkcda_totalamount"] = new Money(totalAmount);

                if (decimal.TryParse(account.PaymentAmount, out decimal paymentAmount))
                    entity["wkcda_paymentamount"] = new Money(paymentAmount);

                if (decimal.TryParse(account.DiscountAmount, out decimal discountAmount))
                    entity["wkcda_discountamount"] = new Money(discountAmount);

                if (!string.IsNullOrWhiteSpace(account.PaymentType))
                {
                    var paymentTypeValue = CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_paymenttype", account.PaymentType, _logger);
                    if (paymentTypeValue.HasValue)
                        entity["wkcda_paymenttype"] = new OptionSetValue(paymentTypeValue.Value);
                }

                if (!string.IsNullOrWhiteSpace(account.SalesChannel))
                {
                    var salesChannelValue = CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_saleschannel", account.SalesChannel, _logger);
                    if (salesChannelValue.HasValue)
                        entity["wkcda_saleschannel"] = new OptionSetValue(salesChannelValue.Value);
                }

                if (DateTime.TryParse(account.PaymemtDate, out DateTime paymentDate))
                    entity["wkcda_paymentdate"] = paymentDate;
                else
                    entity["wkcda_paymentdate"] = DateTime.Now;

                if (!string.IsNullOrWhiteSpace(account.PaidBy))
                {
                    var paidByContactRef = GetContactReference(svc, account.PaidBy);
                    if (paidByContactRef != null)
                        entity["wkcda_paidby"] = paidByContactRef;
                }

                entity["wkcda_paidby"] = new EntityReference("contact", contactId);

                return svc.Create(entity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating payment transaction");
                throw;
            }
        }

        private Guid CreateMembershipTierHistory(ServiceClient svc, Guid contactId, AccountProfile account)
        {
            if (string.IsNullOrWhiteSpace(account.MembershipTier))
                throw new InvalidPluginExecutionException("Membership tier is required");

            try
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
                    ["wkcda_startdate"] = DateTime.Now
                };

                return svc.Create(entity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating membership tier history");
                throw;
            }
        }

        private string GenerateActivationCode()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var random = new Random();
            var code = new char[8];

            for (int i = 0; i < 8; i++)
            {
                code[i] = chars[random.Next(chars.Length)];
            }

            return new string(code);
        }

        private Guid CreateMembershipActivation(ServiceClient svc, Guid contactId, string activationCode, AccountProfile account)
        {
            try
            {
                var entity = new Entity("wkcda_membershipactivation")
                {
                    ["wkcda_activationcode"] = activationCode,
                    ["wkcda_issuedate"] = DateTime.Now,
                    ["statecode"] = new OptionSetValue(0)
                };

                return svc.Create(entity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating membership activation");
                throw;
            }
        }

        private Guid? CreateGroupRelationship(ServiceClient svc, Guid contactId, AccountProfile account)
        {
            if (string.IsNullOrWhiteSpace(account.MemberGroupRole) || string.IsNullOrWhiteSpace(account.MemberGroupType))
                return null;

            try
            {
                var role = CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershipgrouprelationship", "wkcda_role", account.MemberGroupRole, _logger);
                var recordType = CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershipgrouprelationship", "wkcda_recordtypeid", account.MemberGroupType, _logger);

                if (role == null || recordType == null)
                    return null;

                var entity = new Entity("wkcda_membershipgrouprelationship")
                {
                    ["wkcda_member"] = new EntityReference("contact", contactId),
                    ["wkcda_role"] = new OptionSetValue(role.Value),
                    ["wkcda_recordtypeid"] = new OptionSetValue(recordType.Value)
                };

                return svc.Create(entity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating group relationship");
                return null;
            }
        }

        private void UpdateSubscriptions(ServiceClient svc, Guid contactId, List<SubscriptionInfo> subs)
        {
            if (subs == null || !subs.Any()) return;

            try
            {
                var entity = new Entity("contact", contactId);
                bool hasUpdates = false;

                foreach (var sub in subs)
                {
                    if (string.IsNullOrWhiteSpace(sub.SubscriptionType)) continue;

                    bool value = !sub.IsOptOut;

                    switch (sub.SubscriptionType.ToLower())
                    {
                        case "westkowloonenewsletter":
                            entity["wkcda_westkowloonenewsletter"] = value;
                            hasUpdates = true;
                            break;
                        case "mmagazine":
                            entity["wkcda_mmagazine"] = value;
                            hasUpdates = true;
                            break;
                        case "mmembershipenews":
                        case "mplusmembership":
                            entity["wkcda_mmembershipenews"] = value;
                            hasUpdates = true;
                            break;
                        case "hkpmmembership":
                            entity["wkcda_hkpmmembershipenews"] = value;
                            hasUpdates = true;
                            break;
                        case "mshop":
                            entity["wkcda_mshop"] = value;
                            hasUpdates = true;
                            break;
                        case "mliedm":
                        case "mplus":
                            entity["wkcda_mliedm"] = value;
                            hasUpdates = true;
                            break;
                    }
                }

                if (hasUpdates)
                {
                    svc.Update(entity);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating subscriptions");
            }
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
            [JsonPropertyName("customerEntity")]
            public CustomerEntity customerEntity { get; set; }
        }

        public class CustomerEntity
        {
            [JsonPropertyName("BeforePayment")]
            public bool? BeforePayment { get; set; }

            [JsonPropertyName("AccountProfile")]
            public List<AccountProfile> AccountProfile { get; set; } = new List<AccountProfile>();

            [JsonPropertyName("Login")]
            public bool Login { get; set; }
        }

        public class AccountProfile
        {
            [JsonPropertyName("Email")]
            public string Email { get; set; }

            [JsonPropertyName("FirstName")]
            public string FirstName { get; set; }

            [JsonPropertyName("LastName")]
            public string LastName { get; set; }

            [JsonPropertyName("MobilePhoneNumber")]
            public string MobilePhoneNumber { get; set; }

            [JsonPropertyName("MembershipDetails")]
            public List<MembershipDetail> MembershipDetails { get; set; } = new List<MembershipDetail>();

            [JsonPropertyName("MembershipTier")]
            public string MembershipTier { get; set; }

            [JsonPropertyName("ReferenceNo")]
            public string ReferenceNo { get; set; }

            [JsonPropertyName("IsChild")]
            public bool? IsChild { get; set; }

            [JsonPropertyName("IsStudent")]
            public bool? IsStudent { get; set; }

            [JsonPropertyName("TotalAmount")]
            public string TotalAmount { get; set; }

            [JsonPropertyName("PaymentAmount")]
            public string PaymentAmount { get; set; }

            [JsonPropertyName("DiscountAmount")]
            public string DiscountAmount { get; set; }

            [JsonPropertyName("PaymentType")]
            public string PaymentType { get; set; }

            [JsonPropertyName("SalesChannel")]
            public string SalesChannel { get; set; }

            [JsonPropertyName("PaymemtDate")]
            public string PaymemtDate { get; set; }

            [JsonPropertyName("PaidBy")]
            public string PaidBy { get; set; }

            [JsonPropertyName("MemberGroupType")]
            public string MemberGroupType { get; set; }

            [JsonPropertyName("MemberGroupRole")]
            public string MemberGroupRole { get; set; }

            [JsonPropertyName("SubscriptionInfos")]
            public List<SubscriptionInfo> SubscriptionInfos { get; set; } = new List<SubscriptionInfo>();

            [JsonPropertyName("SrNo")]
            public string SrNo { get; set; }
        }

        public class MembershipDetail
        {
            [JsonPropertyName("MembershipTier")]
            public string MembershipTier { get; set; }

            [JsonPropertyName("ReferenceNo")]
            public string ReferenceNo { get; set; }

            [JsonPropertyName("IsChild")]
            public bool? IsChild { get; set; }

            [JsonPropertyName("IsStudent")]
            public bool? IsStudent { get; set; }

            [JsonPropertyName("TotalAmount")]
            public decimal? TotalAmount { get; set; }

            [JsonPropertyName("PaymentAmount")]
            public decimal? PaymentAmount { get; set; }

            [JsonPropertyName("DiscountAmount")]
            public decimal? DiscountAmount { get; set; }

            [JsonPropertyName("PaymentType")]
            public string PaymentType { get; set; }

            [JsonPropertyName("SalesChannel")]
            public string SalesChannel { get; set; }

            [JsonPropertyName("PaymentDate")]
            public DateTime? PaymentDate { get; set; }

            [JsonPropertyName("PaidBy")]
            public string PaidBy { get; set; }

            [JsonPropertyName("MemberGroupType")]
            public string MemberGroupType { get; set; }

            [JsonPropertyName("MemberGroupRole")]
            public string MemberGroupRole { get; set; }

            [JsonPropertyName("SubscriptionInfos")]
            public List<SubscriptionInfo> SubscriptionInfos { get; set; } = new List<SubscriptionInfo>();
        }

        public class SubscriptionInfo
        {
            [JsonPropertyName("SubscriptionType")]
            public string SubscriptionType { get; set; }

            [JsonPropertyName("SubscriptionDate")]
            public string SubscriptionDate { get; set; }

            [JsonPropertyName("SubscriptionSource")]
            public string SubscriptionSource { get; set; }

            [JsonPropertyName("IsOptOut")]
            public bool IsOptOut { get; set; }
        }

        public class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public string Reference { get; set; }
            public string PaymentHistoryId { get; set; }
            public string MemberTierHistoryId { get; set; }
            public string MasterCustomerID { get; set; }
            public string GroupRelationshipId { get; set; }
            public string ActivationCode { get; set; }
        }

        #endregion
    }
}