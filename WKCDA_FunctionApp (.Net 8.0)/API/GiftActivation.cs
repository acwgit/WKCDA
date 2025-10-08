using Microsoft.AspNetCore.Http;
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

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class GiftActivationWS : WS_Base
    {
        public GiftActivationWS(ILogger<GiftActivationWS> logger) : base(logger) { }

        [Function("GiftActivationWS")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/GiftActivation")] HttpRequest req)
        {
            _logger.LogInformation("APIName: GiftActivationWS");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.customerEntity == null)
                    return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Invalid request body" } });

                // If login is false, return error
                if (requestBody.customerEntity.Login.Equals(false))
                {
                    return new OkObjectResult(new List<ResponseBody_Item>
                {
                    new ResponseBody_Item { Success = false, Remarks = "Please log in your MyWestKowloon Account to continue." }
                });
                }

                var response = await ProcessGiftActivation(svc, requestBody.customerEntity);

                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Invalid JSON" } });
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

        private async Task<List<ResponseBody_Item>> ProcessGiftActivation(ServiceClient svc, CustomerEntity customerEntity)
        {
            var response = new List<ResponseBody_Item>();

            try
            {
                var validationResult = ValidateActivationCode(svc, customerEntity.ActivationCode);
                if (!validationResult.Success)
                {
                    response.Add(new ResponseBody_Item { Success = false, Remarks = validationResult.Remarks });
                    return response;
                }

                var validationResponse = ValidateAccountProfiles(customerEntity, validationResult);
                if (validationResponse != null)
                {
                    response.AddRange(validationResponse);
                    return response;
                }

                UpdateActivationCodeStatus(svc, customerEntity.ActivationCode, "Activated");

                var activationResponse = await ProcessMembershipActivation(svc, customerEntity);
                response.AddRange(activationResponse);
            }
            /*catch (Exception ex)
            {
                _logger.LogError(ex, "Error in gift activation process");
                response.Add(new ResponseBody_Item { Success = false, Remarks = ex.Message });
            }*/
            catch (Exception ex)
            {
                if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"unauthorized: {ex.Message}");
                }
            }

            return response;
        }

        private CodeValidationResult ValidateActivationCode(ServiceClient svc, string activationCode)
        {
            if (string.IsNullOrWhiteSpace(activationCode))
            {
                return new CodeValidationResult { Success = false, Remarks = "Activation code is required" };
            }

            try
            {
                var query = new QueryExpression("wkcda_membershipactivation")
                {
                    ColumnSet = new ColumnSet("wkcda_activationcode", "wkcda_codestatus", "wkcda_membershipactivationid", "wkcda_membershipactivationname"),
                    Criteria = new FilterExpression
                    {

                        Conditions =
                        {
                            new ConditionExpression("wkcda_activationcode", ConditionOperator.Equal, activationCode)
                        }
                    }
                };

                var activationCodes = svc.RetrieveMultiple(query).Entities;
                if (activationCodes.Count == 0)
                {
                    return new CodeValidationResult { Success = false, Remarks = "Invalid activation code" };
                }

                var activationCodeEntity = activationCodes[0];
                //  var codeStatus = activationCodeEntity.GetAttributeValue<string>("wkcda_codestatus");

                var codeStatusLabel = activationCodeEntity.FormattedValues.Contains("wkcda_codestatus")
                    ? activationCodeEntity.FormattedValues["wkcda_codestatus"]
                    : string.Empty;

                if (codeStatusLabel == "Activated")
                {
                    return new CodeValidationResult { Success = false, Remarks = "Activation code has already been used" };
                }

                var memberTierHistories = GetRelatedMemberTierHistories(svc, activationCodeEntity.Id);
                var membershipGroupRef = activationCodeEntity.GetAttributeValue<EntityReference>("wkcda_membershipgroup");

                Guid? membershipGroupId = null;
                if (membershipGroupRef != null)
                {
                    var membershipGroup = svc.Retrieve(
                        "wkcda_membershipgroup",
                        membershipGroupRef.Id,
                        new ColumnSet("wkcda_membershipgroupid")
                    );

                    if (membershipGroup != null && membershipGroup.Contains("wkcda_membershipgroupid"))
                    {
                        membershipGroupId = membershipGroup.GetAttributeValue<Guid>("wkcda_membershipgroupid");
                    }
                }
                return new CodeValidationResult
                {
                    Success = true,
                    AccountProfile = memberTierHistories,
                    GroupId = membershipGroupId?.ToString(),
                    MemberGroupType = activationCodeEntity.GetAttributeValue<string>("wkcda_grouptype")
                };
            }
            /* catch (Exception ex)
             {
                 _logger.LogError(ex, "Error validating activation code");
                 return new CodeValidationResult { Success = false, Remarks = $"Error validating activation code: {ex.Message}" };
             }*/
            catch (Exception ex)
            {
                if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"unauthorized: {ex.Message}");
                }
                throw;
            }
        }

        private List<ResponseBody_Item> ValidateAccountProfiles(CustomerEntity customerEntity, CodeValidationResult validationResult)
        {
            if (customerEntity.AccountProfile == null || customerEntity.AccountProfile.Count == 0)
            {
                return new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "AccountProfile cannot be empty!" } };
            }

            /*if (validationResult.AccountProfile.Count != customerEntity.AccountProfile.Count)
            {
                return new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Input member count does not match purchased gift member count!" } };
            }*/

            // Group validation results by role
            var roleResultMap = new Dictionary<string, List<CustomerResult>>();
            foreach (var cr in validationResult.AccountProfile)
            {
                if (!roleResultMap.ContainsKey(cr.MemberGroupRole))
                {
                    roleResultMap[cr.MemberGroupRole] = new List<CustomerResult>();
                }
                roleResultMap[cr.MemberGroupRole].Add(cr);
            }

            //  var submemberRoleSet = new HashSet<string> { "Add-On Member (Youth)", "Add-On Member (Child)", "Add-On Member (Adult)", "Primary Member" };
            var submemberTierSet = GetSubsidiaryMembershipTiers();

            // Validate each account profile
            DateTime? startDate = null;
            foreach (var account in customerEntity.AccountProfile)
            {
                var validationError = ValidateAccountProfile(account, roleResultMap, submemberTierSet, validationResult, ref startDate); //submemberRoleSet
                if (validationError != null)
                {
                    return new List<ResponseBody_Item> { validationError };
                }
            }

            return null; 
        }

        private ResponseBody_Item ValidateAccountProfile(AccountProfile account,
            Dictionary<string, List<CustomerResult>> roleResultMap, //HashSet<string> submemberRoleSet
            HashSet<string> submemberTierSet, CodeValidationResult validationResult, ref DateTime? startDate)
        {
            if (!account.IsSubsidaryAccount && string.IsNullOrWhiteSpace(account.Email))
            {
                return new ResponseBody_Item { Success = false, Remarks = $"{account.FirstName} {account.LastName}: Email is required for non-subsidiary accounts" };
            }

            if (string.IsNullOrWhiteSpace(account.MemberGroupRole))
            {
                if (account.MemberGroupType != "Individual")
                {
                    return new ResponseBody_Item { Success = false, Remarks = $"{account.Email}: MemberGroupRole cannot be blank for non-Individual members" };
                }
            }

            /* if (!roleResultMap.ContainsKey(account.MemberGroupRole))
             {
                 return new ResponseBody_Item { Success = false, Remarks = $"{account.Email}: cannot find MemberGroupRole {account.MemberGroupRole} in activation code" };
             }

             // Validate group ID
             if (!ValidateGroupId(account, validationResult))
             {
                 return new ResponseBody_Item { Success = false, Remarks = $"{account.Email}: MemberGroupId validation failed" };
             }*/

            // Validate member tier and payment history matches
            var validationErrors = ValidateMemberTierAndPaymentHistory(account, roleResultMap, submemberTierSet);
            if (validationErrors != null)
            {
                return validationErrors;
            }

            return null; // No errors
        }

        /*private bool IsSubsidiaryRoleMatch(Dictionary<string, List<CustomerResult>> roleResultMap, string targetRole, HashSet<string> submemberRoleSet)
        {
            if (!submemberRoleSet.Contains(targetRole)) return false;

            foreach (var roleName in submemberRoleSet)
            {
                if (roleResultMap.ContainsKey(roleName))
                {
                    roleResultMap[targetRole] = roleResultMap[roleName];
                    return true;
                }
            }
            return false;
        }*/

        private bool ValidateGroupId(ServiceClient svc, AccountProfile account, CodeValidationResult validationResult, ILogger logger = null)
        {
            try
            {
                if (account.MemberGroupType != "Individual")
                {
                    if (string.IsNullOrWhiteSpace(account.MemberGroupId))
                    {
                        logger?.LogWarning("Account has no MemberGroupId set.");
                        return false;
                    }

                    if (!Guid.TryParse(account.MemberGroupId, out Guid memberGroupGuid))
                    {
                        logger?.LogWarning($"Invalid MemberGroupId format: {account.MemberGroupId}");
                        return false;
                    }

                    var membershipGroup = svc.Retrieve(
                        "wkcda_membershipgroup",
                        memberGroupGuid,
                        new ColumnSet("wkcda_membershipgroupid")
                    );

                    if (membershipGroup == null || !membershipGroup.Contains("wkcda_membershipgroupid"))
                    {
                        logger?.LogWarning($"No membership group found for ID {account.MemberGroupId}");
                        return false;
                    }

                    var dataverseGroupId = membershipGroup.GetAttributeValue<Guid>("wkcda_membershipgroupid");

                    if (!string.Equals(dataverseGroupId.ToString(), validationResult.GroupId, StringComparison.OrdinalIgnoreCase))
                    {
                        logger?.LogWarning($"GroupId mismatch. Expected {validationResult.GroupId}, found {dataverseGroupId}");
                        return false;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(account.MemberGroupId))
                {
                    logger?.LogWarning("Individual account should not have a MemberGroupId.");
                    return false;
                }

                return validationResult.MemberGroupType == account.MemberGroupType;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error validating GroupId");
                return false;
            }
        }


        private ResponseBody_Item ValidateMemberTierAndPaymentHistory(AccountProfile account,
            Dictionary<string, List<CustomerResult>> roleResultMap, HashSet<string> submemberTierSet)
        {
            _logger.LogInformation($"Starting validation for {account.Email}");
            _logger.LogInformation($"MemberGroupRole: {account.MemberGroupRole}, MemberTierHistoryId: {account.MemberTierHistoryId}, MembershipTier: {account.MembershipTier}");

            bool isMemberTierHistoryIdMatch = false;
            bool isMemberTierNameMatch = false;
            bool isPaymentHistoryIdMatch = false;

            var roleResults = roleResultMap.ContainsKey(account.MemberGroupRole)
                ? roleResultMap[account.MemberGroupRole]
                : new List<CustomerResult>();

            _logger.LogInformation($"Found {roleResults.Count} role results for {account.MemberGroupRole}");

            foreach (var cr in roleResults)
            {
                _logger.LogInformation($"Processing CustomerResult - MemberTierHistoryId: {cr.MemberTierHistoryId}, MemberTierName: {cr.MemberTierName}, PaymentHistoryId: {cr.PaymentHistoryId}");

                // Check MemberTierHistoryId match
                if (IsIdEqual(cr.MemberTierHistoryId, account.MemberTierHistoryId))
                {
                    _logger.LogInformation($"MemberTierHistoryId match found: {cr.MemberTierHistoryId}");
                    if (isMemberTierHistoryIdMatch)
                    {
                        var error = $"{account.Email}: duplicate MemberTierHistoryId {account.MemberTierHistoryId}";
                        _logger.LogWarning(error);
                        return new ResponseBody_Item { Success = false, Remarks = error };
                    }
                    isMemberTierHistoryIdMatch = true;
                }

                if (cr.MemberTierName == account.MembershipTier ||
                    (submemberTierSet.Contains(cr.MemberTierName) && submemberTierSet.Contains(account.MembershipTier)))
                {
                    isMemberTierNameMatch = true;
                }

                if (IsIdEqual(cr.PaymentHistoryId, account.PaymentHistoryId))
                {
                    if (isPaymentHistoryIdMatch)
                    {
                        return new ResponseBody_Item { Success = false, Remarks = $"{account.Email}: duplicate PaymentHistoryId {account.PaymentHistoryId}" };
                    }
                    isPaymentHistoryIdMatch = true;
                }
            }

            /*if (!isMemberTierHistoryIdMatch)
            {
                return new ResponseBody_Item { Success = false, Remarks = $"{account.Email}: MemberTierHistoryId not match with the originally purchased gift MemberTierHistoryId" };
            }

            if (!isMemberTierNameMatch)
            {
                return new ResponseBody_Item { Success = false, Remarks = $"{account.Email}: MemberTierName not match with the originally purchased gift MemberTierName" };
            }

            if (!isPaymentHistoryIdMatch)
            {
                return new ResponseBody_Item { Success = false, Remarks = $"{account.Email}: PaymentHistoryId not match with the originally purchased gift PaymentHistoryId" };
            }
            */
            return null;
        }

        private void UpdateActivationCodeStatus(ServiceClient svc, string activationCode, string status)
        {
            var query = new QueryExpression("wkcda_membershipactivation")
            {
                ColumnSet = new ColumnSet("wkcda_activationcode", "wkcda_codestatus", "wkcda_membershipactivationid", "wkcda_membershipactivationname"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wkcda_activationcode", ConditionOperator.Equal, activationCode)
                    }
                }
            };

            var activationCodes = svc.RetrieveMultiple(query).Entities;
            if (activationCodes.Count > 0)
            {
                int codeStatusValue = status switch
                {
                    "New" => 372120000,
                    "Activated" => 372120002,
                    "Expired" => 100000002,
                    _ => throw new ArgumentException($"Unknown status: {status}")
                };

                var activationCodeEntity = new Entity("wkcda_membershipactivation", activationCodes[0].Id)
                {
                    ["wkcda_codestatus"] = new OptionSetValue(codeStatusValue)
                };

                svc.Update(activationCodeEntity);
            }
        }

        private async Task<List<ResponseBody_Item>> ProcessMembershipActivation(ServiceClient svc, CustomerEntity customerEntity)
        {
            var response = new List<ResponseBody_Item>();

            foreach (var account in customerEntity.AccountProfile)
            {
                try
                {
                    var contactResult = await HandleContact(svc, account);

                    UpdateMembershipTierHistory(svc, account, contactResult.ContactId);

                    var groupRelationshipId = UpdateGroupRelationship(svc, account, contactResult.ContactId);

                    response.Add(new ResponseBody_Item
                    {
                        Success = true,
                        Remarks = contactResult.Email,
                        Reference = account.ReferenceNo,
                        PaymentHistoryId = account.PaymentHistoryId,
                        MemberTierHistoryId = account.MemberTierHistoryId,
                        MasterCustomerID = contactResult.MasterCustomerId,
                        GroupRelationshipId = groupRelationshipId?.ToString() ?? string.Empty,
                        GroupId = account.MemberGroupId,
                        ActivationCode = null
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error processing account {account.Email}");
                    response.Add(new ResponseBody_Item { Success = false, Remarks = ex.Message });
                }
            }

            return response;
        }

        #region CRM Helper Methods

        private async Task<ContactResult> HandleContact(ServiceClient svc, AccountProfile account)
        {
            string email = account.Email;

            if (account.IsSubsidaryAccount && string.IsNullOrWhiteSpace(email))
            {
                email = $"Test{account.LastName}_{account.FirstName}_{DateTime.UtcNow.Ticks}@WestKowloonSubmember.dummy";
            }

            var contactRef = GetContactReference(svc, email);
            Guid contactId;
            string masterCustomerId;

            if (contactRef != null)
            {
                contactId = contactRef.Id;
                var contactEntity = new Entity("contact", contactId)
                {
                    ["firstname"] = account.FirstName,
                    ["lastname"] = account.LastName,
                    ["emailaddress1"] = email,
                    ["mobilephone"] = account.MobilePhoneNumber,
                    ["birthdate"] = GetBirthDate(account.MonthofBirth, account.YearofBirth),
                    ["address1_line1"] = account.Address1,
                    ["address1_line2"] = account.Address2,
                    ["address1_line3"] = account.Address3,
                    ["address1_country"] = account.AddressCountry
                };
                svc.Update(contactEntity);

                var existingContact = svc.Retrieve("contact", contactId, new ColumnSet("wkcda_mastercustomerid"));
                masterCustomerId = existingContact.GetAttributeValue<string>("wkcda_mastercustomerid");
            }
            else
            {
                var contactEntity = new Entity("contact")
                {
                    ["firstname"] = account.FirstName,
                    ["lastname"] = account.LastName,
                    ["emailaddress1"] = email,
                    ["mobilephone"] = account.MobilePhoneNumber,
                    ["birthdate"] = GetBirthDate(account.MonthofBirth, account.YearofBirth),
                    ["address1_line1"] = account.Address1,
                    ["address1_line2"] = account.Address2,
                    ["address1_line3"] = account.Address3,
                    ["address1_country"] = account.AddressCountry,
                    ["wkcda_mastercustomerid"] = $"P{DateTime.UtcNow.ToString("MMddHHmm")}"
                };
                contactId = svc.Create(contactEntity);
                masterCustomerId = contactEntity.GetAttributeValue<string>("wkcda_mastercustomerid");
            }

            if (account.SubscriptionInfos != null && account.SubscriptionInfos.Any())
            {
                UpdateSubscriptions(svc, contactId, account.SubscriptionInfos);
            }



            // Handle PICS infos
            if (account.PICSInfos != null && account.PICSInfos.Any())
            {
                UpdatePICSInfos(svc, contactId, account.PICSInfos);
            }

            return await Task.FromResult(new ContactResult
            {
                ContactId = contactId,
                Email = email,
                MasterCustomerId = masterCustomerId
            });
        }

        private void UpdateMembershipTierHistory(ServiceClient svc, AccountProfile account, Guid contactId)
        {
            if (string.IsNullOrWhiteSpace(account.MemberTierHistoryId) || !Guid.TryParse(account.MemberTierHistoryId, out Guid tierHistoryId))
                return;

            var entity = new Entity("wkcda_membershiptierhistory", tierHistoryId)
            {
                ["wkcda_member"] = new EntityReference("contact", contactId)
            };

            if (account.MembershipEndDate.HasValue)
                entity["wkcda_enddate"] = account.MembershipEndDate.Value;

            svc.Update(entity);
        }

        private Guid? UpdateGroupRelationship(ServiceClient svc, AccountProfile account, Guid contactId)
        {

            if (string.IsNullOrWhiteSpace(account.MemberGroupId) || !Guid.TryParse(account.MemberGroupId, out Guid groupId))
                return null;

            var query = new QueryExpression("wkcda_membershipgrouprelationship")
            {
                ColumnSet = new ColumnSet("wkcda_membershipgrouprelationshipid"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wkcda_member", ConditionOperator.Equal, contactId),
                      //  new ConditionExpression("wkcda_membershipgroup", ConditionOperator.Equal, groupId)
                    }
                }
            };

            var relationships = svc.RetrieveMultiple(query).Entities;
            _logger.LogInformation($"Test validation for {relationships}");

            if (relationships.Count == 0)
            {
                var role = CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershipgrouprelationship", "wkcda_role", account.MemberGroupRole, _logger);
                var recordType = CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershipgrouprelationship", "wkcda_recordtypeid", account.MemberGroupType, _logger);

                if (role != null && recordType != null)
                {
                    var entity = new Entity("wkcda_membershipgrouprelationship")
                    {
                        ["wkcda_member"] = new EntityReference("contact", contactId),
                        ["wkcda_membershipgroup"] = new EntityReference("wkcda_membershipgroup", groupId),
                        ["wkcda_role"] = new OptionSetValue(role.Value),
                        ["wkcda_recordtypeid"] = new OptionSetValue(recordType.Value)
                    };
                    return svc.Create(entity);
                }
            }
            else
            {
                return relationships[0].Id;
            }

            return null;
        }

        private EntityReference GetContactReference(ServiceClient svc, string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;

            var qe = new QueryExpression("contact")
            {
                ColumnSet = new ColumnSet("contactid")
            };
            qe.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, email);

            var ec = svc.RetrieveMultiple(qe);
            return ec.Entities.Count > 0 ? ec.Entities[0].ToEntityReference() : null;
        }

        private List<CustomerResult> GetRelatedMemberTierHistories(ServiceClient svc, Guid activationCodeId)
        {
            var results = new List<CustomerResult>();

            var query = new QueryExpression("wkcda_membershiptierhistory")
            {
                ColumnSet = new ColumnSet("wkcda_membershiptierhistoryid", "wkcda_membershiphistoryname", "wkcda_paymentstatus", "wkcda_membershipgroup"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wkcda_membershipactivation", ConditionOperator.Equal, activationCodeId)
                    }
                }
            };

            var histories = svc.RetrieveMultiple(query).Entities;
            foreach (var history in histories)
            {
                results.Add(new CustomerResult
                {
                    MemberTierHistoryId = history.Id.ToString(),
                    MemberTierName = history.GetAttributeValue<string>("wkcda_membershiphistoryname"),
                    PaymentHistoryId = history.GetAttributeValue<OptionSetValue>("wkcda_paymentstatus")?.ToString(),
                    MemberGroupRole = history.GetAttributeValue<EntityReference>("wkcda_membershipgroup").ToString()
                });
            }

            return results;
        }

        private HashSet<string> GetSubsidiaryMembershipTiers()
        {
            // samples - to be changed dynamically - jec
            return new HashSet<string>
            {
                "M+ Family: Adult",
                "M+ Family: Youth",
                "M+ Family: Child",
                "M+ Family: Senior"
            };
        }

        private DateTime? GetBirthDate(string month, string year)
        {
            if (int.TryParse(month, out int monthInt) && int.TryParse(year, out int yearInt))
            {
                try
                {
                    return new DateTime(yearInt, monthInt, 1);
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        private void UpdateSubscriptions(ServiceClient svc, Guid contactId, List<SubscriptionInfo> subs)
        {
            if (subs == null || !subs.Any()) return;

            var entity = new Entity("contact", contactId);

            foreach (var sub in subs)
            {
                if (string.IsNullOrWhiteSpace(sub.SubscriptionType)) continue;

                bool value = !sub.IsOptOut;

                switch (sub.SubscriptionType.ToLower())
                {
                    case "westkowloonenewsletter":
                        entity["wkcda_westkowloonenewsletter"] = value;
                        break;
                    case "mmagazine":
                        entity["wkcda_mmagazine"] = value;
                        break;
                    case "mmembershipenews":
                    case "mplusmembership":
                        entity["wkcda_mmembershipenews"] = value;
                        break;
                    case "hkpmmembership":
                        entity["wkcda_hkpmmembershipenews"] = value;
                        break;
                    case "mshop":
                        entity["wkcda_mshop"] = value;
                        break;
                    case "mliedm":
                    case "mplus":
                        entity["wkcda_mliedm"] = value;
                        break;
                    case "wkcda":
                        entity["wkcda_westkowloonenewsletter"] = value;
                        break;
                }
            }

            svc.Update(entity);
        }

        private void UpdateConsentInfos(IOrganizationService svc, Guid contactId, List<ConsentInfo> consentInfos, ILogger _logger)
        {
            if (consentInfos == null || consentInfos.Count == 0)
                return;

            // Query the tier history record
            var query = new QueryExpression("wkcda_membershiptierhistory")
            {
                ColumnSet = new ColumnSet("wkcda_optinchannelcollateral", "wkcda_optinchanneloffers")
            };
            query.Criteria.AddCondition("wkcda_contactid", ConditionOperator.Equal, contactId);

            var tierHistories = svc.RetrieveMultiple(query).Entities;

            if (tierHistories.Count > 0)
            {
                var tierHistory = new Entity("wkcda_membershiptierhistory", tierHistories[0].Id);

                foreach (var consent in consentInfos)
                {
                    if (!DateTime.TryParse(consent.ConsentDate, out DateTime consentDate))
                        continue;

                    switch (consent.ConsentType.Trim().ToLower())
                    {
                        case "collateral":
                            // inline mapping style
                            tierHistory["wkcda_optinchannelcollateral"] =
                                new OptionSetValue(
                                    CRMEntityHelper.getOptionSetValue(
                                        svc,
                                        "wkcda_membershiptierhistory",
                                        "wkcda_optinchannelcollateral",
                                        consent.ConsentSource,
                                        _logger
                                    ) ?? 0
                                );
                            break;

                        case "offers":
                            tierHistory["wkcda_optinchanneloffers"] =
                                new OptionSetValue(
                                    CRMEntityHelper.getOptionSetValue(
                                        svc,
                                        "wkcda_membershiptierhistory",
                                        "wkcda_optinchanneloffers",
                                        consent.ConsentSource,
                                        _logger
                                    ) ?? 0
                                );
                            break;

                        default:
                            _logger?.LogWarning($"Unknown ConsentType: {consent.ConsentType}");
                            break;
                    }
                }

                svc.Update(tierHistory);
            }
        }


        /*   private OptionSetValue MapConsentSource(IOrganizationService svc, string consentSource, ILogger _logger = null)
            {
                if (string.IsNullOrWhiteSpace(consentSource))
                    return null;

                // Use CRMEntityHelper to look up the option set value dynamically
                var optionValue = CRMEntityHelper.getOptionSetValue(
                    svc,
                    "wkcda_membershiptierhistory",                  // entity logical name
                    "wkcda_optinchannelcollateral",      // field logical name (replace with your actual consent source field)
                    consentSource,              // the text value coming from input
                    _logger
                );

                return optionValue != null ? new OptionSetValue(optionValue.Value) : null;
            }*/



        private void UpdatePICSInfos(ServiceClient svc, Guid contactId, List<PICSInfo> picsInfos)
        {
            if (picsInfos == null || !picsInfos.Any()) return;

            foreach (var pics in picsInfos)
            {
                if (DateTime.TryParse(pics.PICSDate, out DateTime picsDate))
                {
                    var entity = new Entity("wkcda_picsinfo")
                    {
                        ["wkcda_contact"] = new EntityReference("contact", contactId),
                        ["wkcda_picstype"] = pics.PICSType,
                        ["wkcda_picsdate"] = picsDate,
                        ["wkcda_source"] = pics.PICSSource
                    };
                    svc.Create(entity);
                }
            }
        }

        private bool IsIdEqual(string id1, string id2)
        {
            if (string.IsNullOrWhiteSpace(id1) || string.IsNullOrWhiteSpace(id2))
                return false;

            return Guid.TryParse(id1, out Guid guid1) && Guid.TryParse(id2, out Guid guid2) && guid1 == guid2;
        }

        #endregion

        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                { "", "" },
            };
        }

        #region Helper Classes

        public class RequestBody
        {
            [JsonPropertyName("customerEntity")]
            public CustomerEntity customerEntity { get; set; }
        }

        public class CustomerEntity
        {
            [JsonPropertyName("PromoCode")]
            public string PromoCode { get; set; }

            [JsonPropertyName("ActivationCode")]
            public string ActivationCode { get; set; }

            [JsonPropertyName("Login")]
            public bool Login { get; set; }

            [JsonPropertyName("AccountProfile")]
            public List<AccountProfile> AccountProfile { get; set; } = new List<AccountProfile>();
        }

        public class AccountProfile
        {
            [JsonPropertyName("Email")]
            public string Email { get; set; }

            [JsonPropertyName("Salutation")]
            public string Salutation { get; set; }

            [JsonPropertyName("FirstName")]
            public string FirstName { get; set; }

            [JsonPropertyName("LastName")]
            public string LastName { get; set; }

            [JsonPropertyName("MonthofBirth")]
            public string MonthofBirth { get; set; }

            [JsonPropertyName("YearofBirth")]
            public string YearofBirth { get; set; }

            [JsonPropertyName("MobilePhoneCountryCode")]
            public string MobilePhoneCountryCode { get; set; }

            [JsonPropertyName("MobilePhoneNumber")]
            public string MobilePhoneNumber { get; set; }

            [JsonPropertyName("PreferredLanguage")]
            public string PreferredLanguage { get; set; }

            [JsonPropertyName("Password")]
            public string Password { get; set; }

            [JsonPropertyName("Address1")]
            public string Address1 { get; set; }

            [JsonPropertyName("Address2")]
            public string Address2 { get; set; }

            [JsonPropertyName("Address3")]
            public string Address3 { get; set; }

            [JsonPropertyName("AddressCountry")]
            public string AddressCountry { get; set; }

            [JsonPropertyName("AltCntcFirstName")]
            public string AltCntcFirstName { get; set; }

            [JsonPropertyName("AltCntcLastName")]
            public string AltCntcLastName { get; set; }

            [JsonPropertyName("AltCntcEmail")]
            public string AltCntcEmail { get; set; }

            [JsonPropertyName("AltCntcMobileCountryCode")]
            public string AltCntcMobileCountryCode { get; set; }

            [JsonPropertyName("AltCntcMobile")]
            public string AltCntcMobile { get; set; }

            [JsonPropertyName("CustomerSource")]
            public string CustomerSource { get; set; }

            [JsonPropertyName("IsSubsidaryAccount")]
            public bool IsSubsidaryAccount { get; set; }

            [JsonPropertyName("MembershipTier")]
            public string MembershipTier { get; set; }

            [JsonPropertyName("ReferenceNo")]
            public string ReferenceNo { get; set; }

            [JsonPropertyName("IsChild")]
            public bool IsChild { get; set; }

            [JsonPropertyName("IsStudent")]
            public bool IsStudent { get; set; }

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

            [JsonPropertyName("MemberTierHistoryId")]
            public string MemberTierHistoryId { get; set; }

            [JsonPropertyName("MemberGroupId")]
            public string MemberGroupId { get; set; }

            [JsonPropertyName("PaymentHistoryId")]
            public string PaymentHistoryId { get; set; }

            [JsonPropertyName("MembershipEndDate")]
            public DateTime? MembershipEndDate { get; set; }

            [JsonPropertyName("SubscriptionInfos")]
            public List<SubscriptionInfo> SubscriptionInfos { get; set; } = new List<SubscriptionInfo>();

            [JsonPropertyName("ConsentInfos")]
            public List<ConsentInfo> ConsentInfos { get; set; } = new List<ConsentInfo>();

            [JsonPropertyName("PICSInfos")]
            public List<PICSInfo> PICSInfos { get; set; } = new List<PICSInfo>();
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

        public class ConsentInfo
        {
            [JsonPropertyName("ConsentType")]
            public string ConsentType { get; set; }

            [JsonPropertyName("ConsentDate")]
            public string ConsentDate { get; set; }

            [JsonPropertyName("ConsentSource")]
            public string ConsentSource { get; set; }
        }

        public class PICSInfo
        {
            [JsonPropertyName("PICSType")]
            public string PICSType { get; set; }

            [JsonPropertyName("PICSDate")]
            public string PICSDate { get; set; }

            [JsonPropertyName("PICSSource")]
            public string PICSSource { get; set; }
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
            public string GroupId { get; set; }
            public string ActivationCode { get; set; }
        }

        public class CodeValidationResult
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public List<CustomerResult> AccountProfile { get; set; } = new List<CustomerResult>();
            public string GroupId { get; set; }
            public string MemberGroupType { get; set; }
        }

        public class CustomerResult
        {
            public string MemberTierHistoryId { get; set; }
            public string MemberTierName { get; set; }
            public string PaymentHistoryId { get; set; }
            public string MemberGroupRole { get; set; }
        }

        public class ContactResult
        {
            public Guid ContactId { get; set; }
            public string Email { get; set; }
            public string MasterCustomerId { get; set; }
        }

        #endregion
    }
}