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
using System.Reflection.Metadata.Ecma335;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class ComboPaidMembershipPurchaseBeforePayment : WS_Base
    {
        public ComboPaidMembershipPurchaseBeforePayment(ILogger<ComboPaidMembershipPurchaseBeforePayment> logger) : base(logger) { }

        [Function("ComboPaidMembershipPurchaseBeforePayment")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/ComboPaidMembershipPurchaseBeforePayment")] HttpRequest req)
        {
            _logger.LogInformation("APIName: ComboPaidMembershipPurchaseBeforePayment");
            List<MembershipInfoResponse> membersInfo = new List<MembershipInfoResponse>();
            var response = new List<ResponseBody_Item>();
            var resultItem = new ResponseBody_Item();
            List<MembershipInfoResponse> memResp = new List<MembershipInfoResponse>();
            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.customerEntity?.MemberInfo == null || requestBody.customerEntity.MemberInfo.Count == 0)
                    return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Invalid request body" } });
                if (requestBody.customerEntity.MemberInfo.Count > 1)
                {
                    return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "only 1 customer allowed " } });
                }
                
                /*
        public string ReferenceNumber { get; set; }
        public string PromoCode { get; set; }
        public string PaymentHistoryId { get; set; }
        public string MemberTierHistoryId { get; set; }
        public DateTi MembershipStartDate { get; set; }
        public DateTi MembershipEndDate { get; set; }
        public string BusinessUnit { get; set; }
        */




                foreach (var account in requestBody.customerEntity.MemberInfo)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(account.Email))
                        {
                            response.Add(new ResponseBody_Item { Success = false, Remarks = "Email is empty" });
                            continue;
                        }

                        // Check if contact exists
                        var contactRef = GetContactReference(svc, account.Email);
                        Guid contactId;

                        if (contactRef != null)
                        {
                            contactId = contactRef.Id;

                            // Update existing contact
                            var contactEntity = new Entity("contact", contactId)
                            {
                                ["firstname"] = account.FirstName,
                                ["lastname"] = account.LastName,
                                ["emailaddress1"] = account.Email,
                                ["mobilephone"] = account.MobilePhoneNumber
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
                                ["emailaddress1"] = account.Email,
                                ["mobilephone"] = account.MobilePhoneNumber,
                                //["wkcda_mastercustomerid"] = $"P{DateTime.UtcNow.Ticks}"
                            };
                            contactId = svc.Create(contactEntity);
                        }

                        // Membership/payment records
                       
                        
                        var paymentHistoryId = CreatePaymentTransaction(svc, contactId, account, requestBody.customerEntity);
                        var memberTierHistoryId = CreateMembershipTierHistory(svc, contactId, account);
                        var groupRelationshipId = CreateGroupRelationship(svc, contactId, account);
                        var groupId = GetMembershipGroupId(svc, account.MemberGroupType, account.Email);
                        // Retrieve display names
                        string paymentHistoryName = string.Empty;
                        string memberTierHistoryName = string.Empty;
                        string groupRelationshipName = string.Empty;
                        string groupName = string.Empty;

                        if (paymentHistoryId != Guid.Empty)
                        {
                            var ph = svc.Retrieve("wkcda_paymenttransaction", paymentHistoryId, new ColumnSet("wkcda_paymenttransaction"));
                            paymentHistoryName = ph.GetAttributeValue<string>("wkcda_paymenttransaction");
                        }

                        if (memberTierHistoryId.Any())
                        {
                            var mh = svc.Retrieve("wkcda_membershiptierhistory", memberTierHistoryId.First(), new ColumnSet("wkcda_membershiphistoryname"));
                            memberTierHistoryName = mh.GetAttributeValue<string>("wkcda_membershiphistoryname");
                        }

                        if (groupRelationshipId != Guid.Empty)
                        {
                            var gr = svc.Retrieve("wkcda_membershipgrouprelationship", groupRelationshipId, new ColumnSet("wkcda_membershipgrouprelationshipname"));
                            groupRelationshipName = gr.GetAttributeValue<string>("wkcda_membershipgrouprelationshipname");
                        }

                        if (groupId != Guid.Empty)
                        {
                            var g = svc.Retrieve("wkcda_membershipgroup", groupId, new ColumnSet("wkcda_newcolumn"));
                            groupName = g.GetAttributeValue<string>("wkcda_newcolumn");
                        }

                        // Related records
                        if (account.SubscriptionInfos != null)
                        {
                            foreach (var sub in account.SubscriptionInfos)
                                UpdateSubscriptions(svc, contactId, account.SubscriptionInfos);
                        }

                        // Related records
                        if (account.SubscriptionInfos != null)
                        {
                            UpdateSubscriptions(svc, contactId, account.SubscriptionInfos);
                        }

                        if (account.MembershipInfo != null)
                        {
                            if (account.MembershipInfo.Count > 2)
                            {
                                return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Only 2 Business Units are supported for now!" } });
                            }
                            else
                            {
                                
                                foreach (var memInfo in account.MembershipInfo)
                                {
                                    var mebInfoRes = new MembershipInfoResponse()
                                    {
                                        PromoCode = memInfo.PromoCode,
                                        PaymentHistoryId = paymentHistoryName,
                                        MemberTierHistoryId = memberTierHistoryName,
                                        MembershipStartDate = null,
                                        MembershipEndDate = null,
                                        ReferenceNumber = memInfo.ReferenceNo,
                                        BusinessUnit = null
                                    };
                                    membersInfo.Add(mebInfoRes);
                                    UpdateMembershipInfo(svc, contactId, account.MembershipInfo);
                                    

                                }
                                   

                            }
                        }

                        

                        var contactRetrieved = svc.Retrieve("contact", contactId, new ColumnSet("wkcda_mastercustomerid"));
                        var masterCustomerId = contactRetrieved.GetAttributeValue<string>("wkcda_mastercustomerid") ?? string.Empty;
                        response.Add(new ResponseBody_Item
                        {
                            Success = true,
                            Remarks = account.Email,
                            MasterCustomerID = masterCustomerId,
                            MembershipPackage = "Combo",
                            MembershipInfo = membersInfo

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
                        { StatusCode = StatusCodes.Status400BadRequest };
                    }
                }

                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new List<ResponseBody_Item> { new ResponseBody_Item { Success = false, Remarks = "Invalid JSON" } });
            }
            catch (Exception ex)
            {
                return new ObjectResult(new { error = "Bad Request", message = ex.Message }) { StatusCode = StatusCodes.Status400BadRequest };
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
        private Guid CreatePaymentTransaction(ServiceClient svc, Guid contactId, MemberInfo account, CustomerEntity cusEnt)
        {
            var memshipInfo = account.MembershipInfo[0];

            var entity = new Entity("wkcda_paymenttransaction");
            entity["wkcda_totalamount"] = new Money(decimal.Parse(memshipInfo.TotalAmount));
            entity["wkcda_paymentamount"] = new Money(decimal.Parse(memshipInfo.PaymentAmount));
            entity["wkcda_discountamount"] = new Money(decimal.Parse(memshipInfo.DiscountAmount));
            entity["wkcda_paymenttype"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_paymenttype", cusEnt.PaymentType, _logger) ?? 0);
            entity["wkcda_saleschannel"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_saleschannel", cusEnt.SalesChannel, _logger) ?? 0);
            //entity["wkcda_paymentdate"] = DateTime.Parse(cusEnt.PaymemtDate);

            // paid by contact lookup
            var paidByContactRef = GetContactReference(svc, cusEnt.PaidBy);
            if (paidByContactRef != null)
                entity["wkcda_paidby"] = paidByContactRef;

            return svc.Create(entity);


        }
        private List<Guid> CreateMembershipTierHistory(ServiceClient svc, Guid contactId, MemberInfo account)
        {
            // Query wkcda_membershiptier entity by name
            List<Guid> mthIds = new List<Guid>();
            foreach (var mi in account.MembershipInfo)
            {
                var query = new QueryExpression("wkcda_membershiptier")
                {
                    ColumnSet = new ColumnSet("wkcda_membershiptierid"),
                    Criteria = new FilterExpression
                    {
                        Conditions =
            {
                new ConditionExpression("wkcda_membershiptiername", ConditionOperator.Equal, mi.MembershipTier)
            }
                    }
                };

                var tier = svc.RetrieveMultiple(query).Entities.FirstOrDefault();
                if (tier == null)
                {
                    throw new InvalidPluginExecutionException($"Membership Tier '{mi.MembershipTier}' not found.");
                }

                var entity = new Entity("wkcda_membershiptierhistory")
                {
                    ["wkcda_member"] = new EntityReference("contact", contactId),
                    ["wkcda_membershiptier"] = new EntityReference("wkcda_membershiptier", tier.Id)
                };

                mthIds.Add(svc.Create(entity));
            }
            return mthIds;
        }
        private Guid CreateGroupRelationship(ServiceClient svc, Guid contactId, MemberInfo account)
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

            return svc.Create(entity);
        }
        private Guid GetMembershipGroupId(ServiceClient svc, string memberGroupType, string email)
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
        private void UpdateMembershipInfo(ServiceClient svc, Guid contactId, List<MembershipInfo> meminfos)
        {
            var entity = new Entity("contact", contactId);

            foreach (var mi in meminfos)
            {
                if (mi.ConsentInfos != null)
                {
                    var qe = new QueryExpression("wkcda_membershiptierhistory")
                    {
                        ColumnSet = new ColumnSet(
                        "wkcda_optindateanonymous", "wkcda_optinchannelanonymous",
                        "wkcda_optindatecollateral", "wkcda_optinchannelcollateral",
                        "wkcda_optindateguardian", "wkcda_optinchannelguardian")
                    };
                    qe.Criteria.AddCondition("wkcda_member", ConditionOperator.Equal, contactId);
                    var tierHist = svc.RetrieveMultiple(qe).Entities.FirstOrDefault();

                    if (tierHist != null)
                    {
                        foreach (var consent in mi.ConsentInfos)
                        {
                            switch (consent.ConsentType.ToLower())
                            {
                                case "collateral":
                                    tierHist["wkcda_optindatecollateral"] = DateTime.Parse(consent.ConsentDate);
                                    tierHist["wkcda_optinchannelcollateral"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershiptierhistory", "wkcda_optinchannelcollateral", consent.ConsentSource, _logger) ?? 0);
                                    break;
                                case "guardian":
                                    tierHist["wkcda_optindateguardian"] = DateTime.Parse(consent.ConsentDate);
                                    tierHist["wkcda_optinchannelguardian"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershiptierhistory", "wkcda_optinchannelguardian", consent.ConsentSource, _logger) ?? 0);
                                    break;
                                case "anonymous":
                                    tierHist["wkcda_optindateanonymous"] = DateTime.Parse(consent.ConsentDate);
                                    tierHist["wkcda_optinchannelanonymous"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_membershiptierhistory", "wkcda_optinchannelanonymous", consent.ConsentSource, _logger) ?? 0);
                                    break;
                            }
                        }

                        svc.Update(tierHist);
                    }
                }
                if (mi.PICSInfos != null)
                    foreach (var pics in mi.PICSInfos)
                        CreatePICSTransaction(svc, contactId, pics);

            }

        }
        private void UpdateSubscriptions(ServiceClient svc, Guid contactId, List<SubscriptionInfos> subs)
        {
            var entity = new Entity("contact", contactId);

            foreach (var sub in subs)
            {
                bool value = true;

                switch (sub.SubscriptionType.ToLower())
                {
                    case "westkowloonenewsletter":
                        entity["wkcda_westkowloonenewsletter"] = value;
                        break;
                    case "mmagazine":
                        entity["wkcda_mmagazine"] = value;
                        break;
                    case "mmembershipenews":
                    case "mplusmembership": // handle alias
                        entity["wkcda_mmembershipenews"] = value;
                        break;
                    case "hkpmmembership":
                        entity["wkcda_hkpmmembershipenews"] = value;
                        break;
                    case "mshop":
                        entity["wkcda_mshop"] = value;
                        break;
                    case "mliedm":
                    case "mplus": // handle alias
                        entity["wkcda_mliedm"] = value;
                        break;
                }
            }

            svc.Update(entity);
        }
        private void UpdateConsentFields(Entity membershipTierHistoryEntity, ConsentInfos consent)
        {
            switch (consent.ConsentType.ToLower())
            {
                case "collateral":
                    membershipTierHistoryEntity["wkcda_OptInDateCollateral"] = DateTime.Parse(consent.ConsentDate);
                    membershipTierHistoryEntity["OptInChannelCollateral"] = consent.ConsentSource;
                    break;
                case "guardian":
                    membershipTierHistoryEntity["wkcda_OptInDateGuardian"] = DateTime.Parse(consent.ConsentDate);
                    membershipTierHistoryEntity["OptInChannelGuardian"] = consent.ConsentSource;
                    break;
                case "anonymous":
                    membershipTierHistoryEntity["wkcda_OptInDateAnonymous"] = DateTime.Parse(consent.ConsentDate);
                    membershipTierHistoryEntity["OptInChannelAnonymous"] = consent.ConsentSource;
                    break;
            }
        }
        private void CreatePICSTransaction(ServiceClient svc, Guid contactId, PICSInfos pics)
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

        #endregion

        #region Request/Response Classes
        public class RequestBody
        {
            public CustomerEntity customerEntity { get; set; }
        }
        public class CustomerEntityRequest
        {
            public CustomerEntity CustomerEntity { get; set; }
        }

        public class CustomerEntity
        {
            public bool Login { get; set; }
            public string PaidBy { get; set; }
            public string PaymentType { get; set; }
            public string SalesChannel { get; set; }
            public DateTime PaymemtDate { get; set; }
            public bool QuickMode { get; set; }

            public List<MemberInfo> MemberInfo { get; set; }
        }

        public class MemberInfo
        {
            public string Email { get; set; }
            public bool IsNotEncrypt { get; set; }
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
            public bool IsChild { get; set; }
            public bool IsStudent { get; set; }
            public string MemberGroupRole { get; set; }
            public string MemberGroupType { get; set; }

            public List<SubscriptionInfos> SubscriptionInfos { get; set; }
            public List<MembershipInfo> MembershipInfo { get; set; }
        }

        public class SubscriptionInfos
        {
            public string SubscriptionType { get; set; }
            public DateTime? SubscriptionDate { get; set; }
            public string SubscriptionSource { get; set; }
        }

        public class MembershipInfo
        {
            public string PromoCode { get; set; }
            public string BusinessUnit { get; set; }
            public string MembershipTier { get; set; }
            public string ReferenceNo { get; set; }
            public string TotalAmount { get; set; }
            public string PaymentAmount { get; set; }
            public string DiscountAmount { get; set; }

            public List<ConsentInfos> ConsentInfos { get; set; }
            public List<PICSInfos> PICSInfos { get; set; }
        }

        public class ConsentInfos
        {
            public string ConsentType { get; set; }
            public string ConsentDate { get; set; }
            public string ConsentSource { get; set; }
            public string AlternativeName { get; set; }
        }

        public class PICSInfos
        {
            public string PICSType { get; set; }
            public string PICSDate { get; set; }
            public string PICSSource { get; set; }
        }
        public class ResponseBody_Item
        {
            public string MasterCustomerID { get; set; }
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public string MembershipPackage { get; set; }
            public List<MembershipInfoResponse> MembershipInfo { get; set; }
        }
        public class MembershipInfoResponse
        {
            public string PromoCode { get; set; }
            public string PaymentHistoryId { get; set; }
            public string MemberTierHistoryId { get; set; }
            public DateTime? MembershipStartDate { get; set; }
            public DateTime? MembershipEndDate { get; set; }
            public string ReferenceNumber { get; set; }
            public string BusinessUnit { get; set; }
        }
        #endregion
    }
}