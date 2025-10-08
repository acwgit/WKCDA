using Microsoft.AspNetCore.Http;
using DataverseModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System.Text.Json;
using System.Text.Json.Serialization;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using WKCDA_FunctionApp__.Net_8._0_.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class MembershipUpdateActivation : WS_Base
    {
        public MembershipUpdateActivation(ILogger<MembershipUpdateActivation> logger) : base(logger) { }

        [Function("MembershipUpdateActivation")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/MembershipUpdateActivation")] HttpRequest req)
        {
            _logger.LogInformation("APIName: MembershipUpdateActivation");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.CustomerEntity?.AccountProfile == null ||
                    !requestBody.CustomerEntity.AccountProfile.Any())
                {
                    return new OkObjectResult(new ResponseBody_Item
                    {
                        Success = false,
                        Remarks = "Invalid request body",
                        MasterCustomerID = null
                    });
                }

                if (requestBody.CustomerEntity.Login.Equals(false))
                {
                    return new OkObjectResult(new List<ResponseBody_Item>
                    {
                        new ResponseBody_Item { Success = false, Remarks = "Please log in your MyWestKowloon Account to continue." }
                    });
                }

                var response = new List<ResponseBody_Item>();

                foreach (var account in requestBody.CustomerEntity.AccountProfile)
                {
                    var resultItem = new ResponseBody_Item
                    {
                        Success = false,
                        Remarks = account.Email,
                        MasterCustomerID = null
                    };
                    response.Add(resultItem);

                    try
                    {
                        string decodedEmail = DecodeBase64Field(account.Email);
                        string decodedMobilePhone = DecodeBase64Field(account.MobilePhoneNumber);
                        string decodedPassword = DecodeBase64Field(account.Password);

                        if (string.IsNullOrWhiteSpace(decodedEmail))
                        {
                            resultItem.Remarks = "Email is required.";
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(decodedMobilePhone))
                        {
                            resultItem.Remarks = "MobilePhoneNumber is required.";
                            continue;
                        }

                        if (!IsValidPhoneNumber(decodedMobilePhone))
                        {
                            resultItem.Remarks = "MobilePhoneNumber must contain only numbers.";
                            continue;
                        }

                        resultItem.Remarks = decodedEmail;

                        //  _logger.LogInformation($"Decoded values - Email: {decodedEmail}, Mobile: {decodedMobilePhone}, Password: {decodedPassword}");

                        var contactQuery = new QueryExpression("contact")
                        {
                            ColumnSet = new ColumnSet("contactid", "emailaddress1", "wkcda_mastercustomerid"),
                            Criteria = new FilterExpression
                            {
                                Conditions =
                        {
                            new ConditionExpression("emailaddress1", ConditionOperator.Equal, decodedEmail)
                        }
                            }
                        };

                        var contact = svc.RetrieveMultiple(contactQuery).Entities.FirstOrDefault();
                        if (contact == null)
                        {
                            resultItem.Remarks = $"{decodedEmail}: No account with the given email found in database!";
                            continue;
                        }

                        var masterCustomerId = contact.GetAttributeValue<string>("wkcda_mastercustomerid");
                        if (string.IsNullOrEmpty(masterCustomerId))
                        {
                            resultItem.Remarks = "Contact found but MasterCustomerID not set.";
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(decodedMobilePhone))
                        {
                            contact["mobilephone"] = decodedMobilePhone;
                        }

                        if (account.SubscriptionInfos != null)
                        {
                            foreach (var sub in account.SubscriptionInfos)
                            {
                                bool isOptIn = !sub.IsOptOut;

                                string checkboxField = null;
                                string dateField = null;
                                string lookupField = null;

                                string decodedSubscriptionType = DecodeBase64Field(sub.SubscriptionType);
                                string decodedSubscriptionSource = DecodeBase64Field(sub.SubscriptionSource);

                                switch (decodedSubscriptionType.ToLower())
                                {
                                    case "mplus":
                                        checkboxField = "wkcda_mmagazine";
                                        dateField = "wkcda_emailoptindate2";
                                        lookupField = "wkcda_optinchannel2";
                                        break;
                                    case "mshop":
                                        checkboxField = "wkcda_mshop";
                                        dateField = "wkcda_emailoptindate3";
                                        lookupField = "wkcda_optinchannel3";
                                        break;
                                    case "mplusmembership":
                                        checkboxField = "wkcda_mmembershipenews";
                                        dateField = "wkcda_emailoptindate4";
                                        lookupField = "wkcda_optinchannel4";
                                        break;
                                    case "hkpmmembership":
                                        checkboxField = "wkcda_hkpmmembershipenews";
                                        dateField = "wkcda_emailoptindate5";
                                        lookupField = "wkcda_optinchannel5";
                                        break;
                                }

                                if (checkboxField != null)
                                    contact[checkboxField] = isOptIn;

                                if (dateField != null)
                                {
                                    string decodedSubscriptionDate = DecodeBase64Field(sub.SubscriptionDate);
                                    if (DateTime.TryParse(decodedSubscriptionDate, out DateTime subDate))
                                        contact[dateField] = subDate;
                                }

                                if (lookupField != null && !string.IsNullOrWhiteSpace(decodedSubscriptionSource))
                                {
                                    var channelQuery = new QueryExpression("wkcda_optinchannel")
                                    {
                                        ColumnSet = new ColumnSet("wkcda_optinchannelid"),
                                        Criteria = new FilterExpression
                                        {
                                            Conditions =
                                    {
                                        new ConditionExpression("wkcda_channelname", ConditionOperator.Equal, decodedSubscriptionSource)
                                    }
                                        }
                                    };

                                    var channelEntity = svc.RetrieveMultiple(channelQuery).Entities.FirstOrDefault();
                                    if (channelEntity != null)
                                        contact[lookupField] = channelEntity.ToEntityReference();
                                    else
                                        _logger.LogWarning("OptInChannel not found for: {Source}", decodedSubscriptionSource);
                                }
                            }

                            svc.Update(contact);
                        }


                        resultItem.Success = true;
                        resultItem.Remarks = contact.GetAttributeValue<string>("emailaddress1");
                        resultItem.MasterCustomerID = masterCustomerId;
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
                            resultItem.Remarks = $"Error processing account: {ex.Message}";
                            _logger.LogError(ex, "Error processing account with email: {Email}", account.Email);
                        }
                    }
                }

                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new ResponseBody_Item
                {
                    Success = false,
                    Remarks = "Invalid JSON",
                    MasterCustomerID = null
                });
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

        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                { "MembershipStatus", "wkcda_membershipstatus" },
                { "GroupStatus", "wkcda_status" },
                { "PaymentStatus", "wkcda_paymentstatus" },
                { "PaymentType", "wkcda_paymenttype" }
            };
        }
        #region Request/Response Classes
        public class RequestBody
        {
            [JsonPropertyName("customerEntity")]
            public required CustomerEntity CustomerEntity { get; set; }
        }
        public class CustomerEntity
        {
            [JsonPropertyName("Login")]
            public bool Login { get; set; }

            [JsonPropertyName("AccountProfile")]
            public required List<AccountProfileInput> AccountProfile { get; set; }
        }
        public class AccountProfileInput
        {
            public string Email { get; set; }
            public string Salutation { get; set; }
            public string FirstName { get; set; }
            public string LastName { get; set; }
            public string MonthofBirth { get; set; }
            public string YearofBirth { get; set; }
            public string MobilePhoneCountryCode { get; set; }
            public string MobilePhoneNumber { get; set; }
            public string PreferredLanguage { get; set; }
            public string Password { get; set; }
            public string Address1 { get; set; }
            public string Address2 { get; set; }
            public string Address3 { get; set; }
            public string AddressCountry { get; set; }
            public string MemberTierHistoryId { get; set; }

            [JsonPropertyName("SubscriptionInfos")]
            public List<SubscriptionInfo> SubscriptionInfos { get; set; }

            [JsonPropertyName("ConsentInfos")]
            public List<ConsentInfo> ConsentInfos { get; set; }

            [JsonPropertyName("PICSInfos")]
            public List<PICSInfo> PICSInfos { get; set; }
        }
        public class SubscriptionInfo
        {
            public string SubscriptionType { get; set; }
            public string SubscriptionDate { get; set; }
            public string SubscriptionSource { get; set; }
            public bool IsOptOut { get; set; }
        }
        public class ConsentInfo
        {
            public string ConsentType { get; set; }
            public string ConsentDate { get; set; }
            public string ConsentSource { get; set; }
        }
        public class PICSInfo
        {
            public string PICSType { get; set; }
            public string PICSDate { get; set; }
            public string PICSSource { get; set; }
        }
        public class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string? Remarks { get; set; }
            public string? MasterCustomerID { get; set; }
        }
        #endregion
    }
}