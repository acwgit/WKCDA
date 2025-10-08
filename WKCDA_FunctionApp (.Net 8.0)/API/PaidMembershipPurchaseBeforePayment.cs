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
using System.Text;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class PaidMembershipPurchaseBeforePayment : WS_Base
    {
        public PaidMembershipPurchaseBeforePayment(ILogger<PaidMembershipPurchaseBeforePayment> logger) : base(logger) { }

        [Function("PaidMembershipPurchaseBeforePayment")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/PaidMembershipPurchaseBeforePayment")] HttpRequest req)
        {
            _logger.LogInformation("APIName: PaidMembershipPurchaseBeforePayment");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.customerEntity?.AccountProfile == null || requestBody.customerEntity.AccountProfile.Count == 0)
                    return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Invalid request body" } });

                if (requestBody.customerEntity.Login.Equals(false))
                {
                     return new BadRequestObjectResult(new { success = false, remarks = "Please log in your MyWestKowloon Account to continue." });
                }

                var response = new List<ResponseBody_Item>();

                foreach (var account in requestBody.customerEntity.AccountProfile)
                {
                    try
                    {
                        string decodedEmail = DecodeBase64Field(account.Email);
                        string decodedMobilePhone = DecodeBase64Field(account.MobilePhoneNumber)?.Trim();
                        string decodedPassword = DecodeBase64Field(account.Password);

                        if (string.IsNullOrWhiteSpace(decodedEmail))
                        {
                            return new BadRequestObjectResult(new { success = false, remarks = "Email is required." });
                        }

                        if (string.IsNullOrWhiteSpace(decodedMobilePhone))
                        {
                            return new BadRequestObjectResult(new { success = false, remarks = "MobilePhoneNumber is required." });
                        }

                        if (!IsValidPhoneNumber(decodedMobilePhone))
                        {
                            return new BadRequestObjectResult(new { success = false, remarks = "MobilePhoneNumber must contain only numbers." });
                        }

                        // Check if contact exists
                        var contactRef = GetContactReference(svc, decodedEmail);
                        Guid contactId;

                        if (contactRef != null)
                        {
                            contactId = contactRef.Id;

                            // Update existing contact
                            var contactEntity = new Entity("contact", contactId)
                            {
                                ["firstname"] = account.FirstName,
                                ["lastname"] = account.LastName,
                                ["emailaddress1"] = decodedEmail,
                                ["mobilephone"] = decodedMobilePhone
                            };
                            svc.Update(contactEntity);
                        }
                        else
                        {
                            // Create new contact
                            var contactEntity = new Entity("contact")
                            {
                                ["firstname"] = account.FirstName,
                                ["lastname"] = account.LastName,
                                ["emailaddress1"] = decodedEmail,
                                ["mobilephone"] = decodedMobilePhone,
                                ["wkcda_mastercustomerid"] = $"P{DateTime.UtcNow.Ticks}"
                            };
                            contactId = svc.Create(contactEntity);
                        }

                         // _logger.LogInformation($"Decoded values - Email: {decodedEmail}, Mobile: {decodedMobilePhone}, Password: {decodedPassword}");

                        // Membership/payment records
                        var paymentHistoryId = CreatePaymentTransaction(svc, contactId, account);

                        // Determine group & relationship
                        Guid groupId = Guid.Empty;
                        Guid groupRelationshipId = Guid.Empty;

                        if (!string.IsNullOrWhiteSpace(account.MemberGroupType))
                        {
                            // create or find the Membership Group
                            groupId = GetOrCreateMembershipGroup(svc, account.MemberGroupType, account.Email);

                            // retrieve the Membership Group to check its type
                            var membershipGroup = svc.Retrieve("wkcda_membershipgroup", groupId, new ColumnSet("wkcda_recordtype"));

                            var recordType = membershipGroup.GetAttributeValue<OptionSetValue>("wkcda_recordtype")?.Value;

                            // get all members in this group (using lookup field wkcda_membershipgroup)
                            var groupMembers = svc.RetrieveMultiple(new QueryExpression("wkcda_membershipgrouprelationship")
                            {
                                ColumnSet = new ColumnSet("wkcda_membershipgrouprelationshipid", "wkcda_role"),
                                Criteria =
                                {
                                    Conditions =
                                    {
                                        new ConditionExpression("wkcda_membershipgroup", ConditionOperator.Equal, groupId)
                                    }
                                }
                            }).Entities;

                            // find the OptionSet value for "Primary Member"
                            var primaryMemberValue = CRMEntityHelper.getOptionSetValue(
                                svc, "wkcda_membershipgrouprelationship", "wkcda_role", "Primary Member", _logger);

                            switch (account.MemberGroupType.Trim().ToLower())
                            {
                                case "family":
                                    // max 7 members
                                    if (groupMembers.Count >= 7)
                                        throw new InvalidOperationException("Family group cannot have more than 7 members.");

                                    // only 1 Primary Member
                                    if (account.MemberGroupRole.Trim().Equals("Primary Member", StringComparison.OrdinalIgnoreCase) &&
                                        groupMembers.Any(m => m.GetAttributeValue<OptionSetValue>("wkcda_role")?.Value == primaryMemberValue))
                                    {
                                        throw new InvalidOperationException("Only 1 Primary Member is allowed in a Family group.");
                                    }
                                    break;

                                case "dual":
                                    // max 2 members
                                    if (groupMembers.Count >= 2)
                                        throw new InvalidOperationException("Dual group cannot have more than 2 members.");

                                    // only 1 Primary Member
                                    if (account.MemberGroupRole.Trim().Equals("Primary Member", StringComparison.OrdinalIgnoreCase) &&
                                        groupMembers.Any(m => m.GetAttributeValue<OptionSetValue>("wkcda_role")?.Value == primaryMemberValue))
                                    {
                                        throw new InvalidOperationException("Only 1 Primary Member is allowed in a Dual group.");
                                    }
                                    break;
                            }

                            // If validation passed → create the relationship record
                            groupRelationshipId = CreateGroupRelationship(svc, contactId, account, groupId);
                        }
                        var memberTierHistoryId = CreateMembershipTierHistory(svc, contactId, account, groupId, groupRelationshipId);
                        // Retrieve the names instead of GUIDs
                        string paymentHistoryName = string.Empty;
                        string memberTierHistoryName = string.Empty;
                        string groupRelationshipName = string.Empty;
                        string groupName = string.Empty;

                        if (paymentHistoryId != Guid.Empty)
                        {
                            var ph = svc.Retrieve("wkcda_paymenttransaction", paymentHistoryId, new ColumnSet("wkcda_paymenttransaction"));
                            paymentHistoryName = ph.GetAttributeValue<string>("wkcda_paymenttransaction");
                        }

                        if (memberTierHistoryId != Guid.Empty)
                        {
                            var mh = svc.Retrieve("wkcda_membershiptierhistory", memberTierHistoryId, new ColumnSet("wkcda_membershiphistoryname"));
                            memberTierHistoryName = mh.GetAttributeValue<string>("wkcda_membershiphistoryname");
                        }

                        if (groupRelationshipId != Guid.Empty)
                        {
                            var gr = svc.Retrieve("wkcda_membershipgrouprelationship", groupRelationshipId, new ColumnSet("wkcda_membershipgrouprelationshipname"));
                            groupRelationshipName = gr.GetAttributeValue<string>("wkcda_membershipgrouprelationshipname");
                        }
                        if (groupId != Guid.Empty)
                        {
                            var group = svc.Retrieve("wkcda_membershipgroup", groupId, new ColumnSet("wkcda_newcolumn"));
                            groupName = group.GetAttributeValue<string>("wkcda_newcolumn");
                        }

                        if (account.SubscriptionInfos != null)
                        {
                            UpdateSubscriptions(svc, contactId, account.SubscriptionInfos);
                        }

                        if (account.ConsentInfos != null && !string.IsNullOrWhiteSpace(account.MembershipTier))
                        {
                            // Find the membership tier history record by tier name and contact
                            var query = new QueryExpression("wkcda_membershiptierhistory")
                            {
                                ColumnSet = new ColumnSet(true),
                                Criteria = new FilterExpression
                                {
                                    Conditions =
                                    {
                                        new ConditionExpression("wkcda_membershiptiername", ConditionOperator.Equal, account.MembershipTier),
                                        new ConditionExpression("wkcda_member", ConditionOperator.Equal, contactId)
                                    }
                                }
                            };

                            var tierHistory = svc.RetrieveMultiple(query).Entities.FirstOrDefault();

                            if (tierHistory != null)
                            {
                                foreach (var consent in account.ConsentInfos)
                                {
                                    UpdateConsentFields(svc, tierHistory, consent);
                                }
                                svc.Update(tierHistory);
                            }
                            else
                            {
                                _logger.LogWarning("MembershipTierHistory not found for contact {Email} and tier {Tier}", account.Email, account.MembershipTier);
                            }
                        }

                        if (account.PICSInfos != null)
                            foreach (var pics in account.PICSInfos)
                                CreatePICSTransaction(svc, contactId, pics);

                        var contactRetrieved = svc.Retrieve("contact", contactId, new ColumnSet("wkcda_mastercustomerid"));
                        var masterCustomerId = contactRetrieved.GetAttributeValue<string>("wkcda_mastercustomerid") ?? string.Empty;
                        response.Add(new ResponseBody_Item
                        {
                            Success = true,
                            Remarks = decodedEmail,
                            Reference = account.ReferenceNo,
                            PaymentHistoryId = paymentHistoryName,
                            MemberTierHistoryId = memberTierHistoryName,
                            MasterCustomerID = masterCustomerId,
                            GroupRelationshipId = groupRelationshipName,
                            GroupId = groupName
                        });
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
                        { StatusCode = StatusCodes.Status400BadRequest }; ;
                    }
                }

                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Invalid JSON" } });
            }
            /* catch (Exception ex)
             {
                 return new ObjectResult(new { error = "Bad Request", message = ex.Message }) { StatusCode = StatusCodes.Status400BadRequest };
             }*/
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
                { "", "" },
            };
        }
        #region CRM Helpers

        private EntityReference? GetContactReference(ServiceClient svc, string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return null;

            var qe = new QueryExpression("contact") { ColumnSet = new ColumnSet("contactid") };
            qe.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, email);

            var ec = svc.RetrieveMultiple(qe);
            return ec.Entities.Count > 0 ? ec.Entities[0].ToEntityReference() : null;
        }
        private Guid CreatePaymentTransaction(ServiceClient svc, Guid contactId, AccountProfile account)
        {
            var entity = new Entity("wkcda_paymenttransaction");
            entity["wkcda_totalamount"] = new Money(decimal.Parse(account.TotalAmount));
            entity["wkcda_paymentamount"] = new Money(decimal.Parse(account.PaymentAmount));
            entity["wkcda_discountamount"] = new Money(decimal.Parse(account.DiscountAmount));
            entity["wkcda_paymenttype"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_paymenttype", account.PaymentType, _logger) ?? 0);
            entity["wkcda_saleschannel"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_saleschannel", account.SalesChannel, _logger) ?? 0);
            entity["wkcda_paymentdate"] = DateTime.Parse(account.PaymemtDate);

            var paidByContactRef = GetContactReference(svc, account.PaidBy);
            if (paidByContactRef != null)
                entity["wkcda_paidby"] = paidByContactRef;

            return svc.Create(entity);
        }
        private Guid CreateMembershipTierHistory(ServiceClient svc, Guid contactId, AccountProfile account, Guid groupId, Guid groupRelationshipId)
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
                ["wkcda_membershiptier"] = new EntityReference("wkcda_membershiptier", tier.Id)
            };
            if (!string.IsNullOrWhiteSpace(account.ReferenceNo))
                entity["wkcda_referenceno"] = account.ReferenceNo;

            if (!string.IsNullOrWhiteSpace(account.PaidBy))
            {
                var payerQuery = new QueryExpression("contact")
                {
                    ColumnSet = new ColumnSet("contactid"),
                    Criteria = new FilterExpression
                    {
                        Conditions =
                {
                    new ConditionExpression("emailaddress1", ConditionOperator.Equal, account.PaidBy)
                }
                    }
                };

                var payer = svc.RetrieveMultiple(payerQuery).Entities.FirstOrDefault();
                if (payer != null)
                {
                    entity["wkcda_paidby"] = payer.ToEntityReference();
                }
            }
            if (groupId != Guid.Empty)
                entity["wkcda_membershipgroup"] = new EntityReference("wkcda_membershipgroup", groupId);

            if (groupRelationshipId != Guid.Empty)
                entity["wkcda_membershipgrouprelationship"] = new EntityReference("wkcda_membershipgrouprelationship", groupRelationshipId);

            var role = CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershiptierhistory", "wkcda_membershiprole", account.MemberGroupRole, _logger);
            if (role != null)
                entity["wkcda_membershiprole"] = new OptionSetValue(role.Value);

            if (!string.IsNullOrWhiteSpace(account.MemberGroupType))
                entity["wkcda_grouptype"] = account.MemberGroupType;

            return svc.Create(entity);
        }
        private Guid CreateGroupRelationship(ServiceClient svc, Guid contactId, AccountProfile account, Guid groupId)
        {
            var role = CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershipgrouprelationship", "wkcda_role", account.MemberGroupRole, _logger);
            var recordType = CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershipgrouprelationship", "wkcda_recordtypeid", account.MemberGroupType, _logger);

            if (role == null || recordType == null)
                throw new Exception("Invalid Role or RecordType");

            var entity = new Entity("wkcda_membershipgrouprelationship")
            {
                ["wkcda_member"] = new EntityReference("contact", contactId),
                ["wkcda_role"] = new OptionSetValue(role.Value),
                ["wkcda_recordtypeid"] = new OptionSetValue(recordType.Value)
            };
            if (groupId != Guid.Empty)
                entity["wkcda_membershipgroup"] = new EntityReference("wkcda_membershipgroup", groupId);

            return svc.Create(entity);
        }

        private Guid GetOrCreateMembershipGroup(ServiceClient svc, string memberGroupType, string email)
        {
            var val = CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershipgroup", "wkcda_recordtype", memberGroupType, _logger);
            if (val == null) return Guid.Empty;

            var qe = new QueryExpression("wkcda_membershipgroup")
            {
                ColumnSet = new ColumnSet("wkcda_membershipgroupid")
            };
            qe.Criteria.AddCondition("wkcda_recordtype", ConditionOperator.Equal, val.Value);

            var ec = svc.RetrieveMultiple(qe);
            if (ec.Entities.Count > 0)
                return ec.Entities[0].Id;

            var newGroup = new Entity("wkcda_membershipgroup")
            {
                ["wkcda_newcolumn"] = $"{memberGroupType}",
                ["wkcda_recordtype"] = new OptionSetValue(val.Value),
                ["statecode"] = new OptionSetValue(0), // active
            };
            return svc.Create(newGroup);
        }
        private void UpdateSubscriptions(ServiceClient svc, Guid contactId, List<SubscriptionInfo> subs)
        {
            var contact = new Entity("contact", contactId);

            foreach (var sub in subs)
            {
                bool isOptIn = !sub.IsOptOut;

                string checkboxField = null;
                string dateField = null;
                string lookupField = null;

                switch (sub.SubscriptionType.ToLower())
                {
                    case "westkowloonenewsletter":
                        checkboxField = "wkcda_westkowloonenewsletter";
                        dateField = "wkcda_emailoptindate1";
                        lookupField = "wkcda_optinchannel1";
                        break;
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
                    case "mliedm":
                        checkboxField = "wkcda_mliedm";
                        dateField = "wkcda_emailoptindate6";
                        lookupField = "wkcda_optinchannel6";
                        break;
                }
                if (checkboxField != null)
                    contact[checkboxField] = isOptIn;

                if (dateField != null && DateTime.TryParse(sub.SubscriptionDate, out DateTime subDate))
                    contact[dateField] = subDate;

                if (lookupField != null && !string.IsNullOrWhiteSpace(sub.SubscriptionSource))
                {
                    var channelQuery = new QueryExpression("wkcda_optinchannel")
                    {
                        ColumnSet = new ColumnSet("wkcda_optinchannelid"),
                        Criteria = { Conditions = { new ConditionExpression("wkcda_channelname", ConditionOperator.Equal, sub.SubscriptionSource) } }
                    };

                    var channelEntity = svc.RetrieveMultiple(channelQuery).Entities.FirstOrDefault();
                    if (channelEntity != null)
                        contact[lookupField] = channelEntity.ToEntityReference();
                    else
                        _logger.LogWarning("OptInChannel not found for: {Source}", sub.SubscriptionSource);
                }
            }

            svc.Update(contact);
        }
        private void UpdateConsentFields(ServiceClient svc, Entity membershipTierHistoryEntity, ConsentInfo consent)
        {
            if (membershipTierHistoryEntity == null || consent == null) return;
            if (string.IsNullOrWhiteSpace(consent.ConsentDate)) return;
            if (!DateTime.TryParse(consent.ConsentDate, out DateTime consentDate)) return;

            bool isOptIn = true;

            switch (consent.ConsentType?.Trim().ToLower())
            {
                case "collateral":
                    membershipTierHistoryEntity["wkcda_collateral"] = isOptIn;
                    membershipTierHistoryEntity["wkcda_optindatecollateral"] = consentDate;
                    membershipTierHistoryEntity["wkcda_optinchannelcollateral"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershiptierhistory", "wkcda_optinchannelcollateral", consent.ConsentSource, _logger) ?? 0);
                    break;

                case "anonymous":
                    membershipTierHistoryEntity["wkcda_anonymous"] = isOptIn;
                    membershipTierHistoryEntity["wkcda_optindateanonymous"] = consentDate;
                    membershipTierHistoryEntity["wkcda_optinchannelanonymous"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershiptierhistory", "wkcda_optinchannelanonymous", consent.ConsentSource, _logger) ?? 0);
                    break;

                case "guardian":
                    membershipTierHistoryEntity["wkcda_guardianconsent"] = isOptIn;
                    membershipTierHistoryEntity["wkcda_optindateguardian"] = consentDate;
                    membershipTierHistoryEntity["wkcda_optinchannelguardian"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershiptierhistory", "wkcda_optinchannelguardian", consent.ConsentSource, _logger) ?? 0);
                    break;

                default:
                    _logger.LogWarning("Unknown consent type: {ConsentType}", consent.ConsentType);
                    break;
            }
        }
        private void CreatePICSTransaction(ServiceClient svc, Guid contactId, PICSInfo pics)
        {
            // Lookup PIC type
            var qe = new QueryExpression("wkcda_pics")
            {
                ColumnSet = new ColumnSet("wkcda_picsid"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wkcda_picsname", ConditionOperator.Equal, pics.PICSType)
                    }
                }
            };
            var pic = svc.RetrieveMultiple(qe).Entities.FirstOrDefault();
            if (pic == null) throw new Exception($"PIC Type '{pics.PICSType}' not found.");

            var entity = new Entity("wkcda_picstransaction")
            {
                ["wkcda_pics"] = new EntityReference("wkcda_pics", pic.Id),
                ["wkcda_optedinmember"] = new EntityReference("contact", contactId),
                ["wkcda_optedindate"] = DateTime.Parse(pics.PICSDate),
                ["wkcda_optinchannelpics"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_picstransaction", "wkcda_optinchannelpics", pics.PICSSource, _logger) ?? 0)
            };
            svc.Create(entity);
        }

        private string DecodeBase64Field(string encodedValue)
        {
            if (string.IsNullOrWhiteSpace(encodedValue))
                return string.Empty;

            try
            {
                byte[] data = Convert.FromBase64String(encodedValue);
                string decodedString = Encoding.UTF8.GetString(data);

                // Extra sanity check: decoded string should not contain replacement chars �
                if (decodedString.Contains('\uFFFD'))
                {
                    _logger.LogInformation($"Decoded string contained invalid chars, treating as plain text: {encodedValue}");
                    return encodedValue;
                }

                _logger.LogInformation($"Successfully decoded Base64: {encodedValue} -> {decodedString}");
                return decodedString;
            }
            catch
            {
                // If decoding fails, just return the original string
                _logger.LogInformation($"Not a valid Base64, returning as-is: {encodedValue}");
                return encodedValue;
            }
        }

        private bool IsBase64String(string base64)
        {
            if (string.IsNullOrEmpty(base64) || base64.Length % 4 != 0)
                return false;

            try
            {
                byte[] data = Convert.FromBase64String(base64);

                // Extra validation: check if decoded string is valid UTF-8
                string decoded = Encoding.UTF8.GetString(data);
                foreach (char c in decoded)
                {
                    if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')
                    {
                        return false;
                    }
                }

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

            string cleaned = phoneNumber.Trim();
            return cleaned.All(char.IsDigit);
        }

        #endregion

        #region Request/Response Classes
        public class RequestBody
        {
            public CustomerEntity customerEntity { get; set; }
        }
        public class CustomerEntity
        {
            public string PromoCode { get; set; }
            public bool Login { get; set; }
            public List<AccountProfile> AccountProfile { get; set; }
        }
        public class AccountProfile
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
            public string AltCntcFirstName { get; set; }
            public string AltCntcLastName { get; set; }
            public string AltCntcEmail { get; set; }
            public string AltCntcMobileCountryCode { get; set; }
            public string AltCntcMobile { get; set; }
            public string CustomerSource { get; set; }
            public bool IsSubsidaryAccount { get; set; }
            public string MembershipTier { get; set; }
            public string ReferenceNo { get; set; }
            public bool IsChild { get; set; }
            public bool IsStudent { get; set; }
            public string TotalAmount { get; set; }
            public string PaymentAmount { get; set; }
            public string DiscountAmount { get; set; }
            public string PaymentType { get; set; }
            public string SalesChannel { get; set; }
            public string PaymemtDate { get; set; }
            public string PaidBy { get; set; }
            public string MemberGroupType { get; set; }
            public string MemberGroupRole { get; set; }
            public List<SubscriptionInfo> SubscriptionInfos { get; set; }
            public List<ConsentInfo> ConsentInfos { get; set; }
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
            public string? PaymentHistoryId { get; set; }
            public string? MemberTierHistoryId { get; set; }
            public string? MembershipStartDate { get; set; }
            public string? MembershipEndDate { get; set; }
            public bool? Renewal { get; set; }
            public string? Reference { get; set; }
            public string? PromoCode { get; set; }
            public string? GroupRelationshipId { get; set; }
            public string? GroupId { get; set; }
            public string? ActivationCode { get; set; }
        }
        #endregion
    }
}