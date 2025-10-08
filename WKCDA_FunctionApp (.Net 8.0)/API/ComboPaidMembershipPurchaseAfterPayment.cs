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

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class ComboPaidMembershipPurchaseAfterPayment : WS_Base
    {
        public ComboPaidMembershipPurchaseAfterPayment(ILogger<ComboPaidMembershipPurchaseAfterPayment> logger) : base(logger) { }

        [Function("ComboPaidMembershipPurchaseAfterPayment")]
        public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/ComboPaidMembershipPurchaseAfterPayment")] HttpRequest req)
        {
            _logger.LogInformation("APIName: ComboPaidMembershipPurchaseAfterPayment");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.AccountProfiles == null || !requestBody.AccountProfiles.Any())
                    return new OkObjectResult(new ResponseBody_Item { Success = false, Remarks = "Invalid request body" });

                var response = new List<ResponseBody_Item>();
                var resultItem = new ResponseBody_Item();
                List<MembershipInfoResponse> memResp = new List<MembershipInfoResponse>();

                foreach (var account1 in requestBody.AccountProfiles)
                {
                    foreach (var account in account1.MembershipInfo)
                    {
                        var memshipinfores = new MembershipInfoResponse()
                        {
                            ReferenceNumber = account.ReferenceNumber,
                            MembershipEndDate = account.MembershipEndDate,
                            MembershipStartDate = account.MembershipStartDate,
                            MemberTierHistoryId = account.MemberTierHistoryId,
                            PaymentHistoryId = account.PaymentHistoryId
                        };
                        memResp.Add(memshipinfores);
                        //MembershipInfo = account.MembershipInfo,
                        resultItem.Success = false;
                        resultItem.Remarks = null;
                        resultItem.MasterCustomerID = account1.MasterCustomerId;
                        //resultItem.MembershipInfo = memshipinfores;



                        // Check required fields
                        if (string.IsNullOrWhiteSpace(account.PaymentHistoryId))
                        {
                            resultItem.Remarks = $"Failed: {account1.Email} PaymentHistoryId Not found";
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(account.MemberTierHistoryId))
                        {
                            resultItem.Remarks = $"Failed: {account1.Email} MemberTierHistoryId Not found";
                            continue;
                        }

                       

                        try
                        {
                            // Get Contact
                            var contactRef = GetContactReference(svc, account1.MasterCustomerId, account1.Email);
                            if (contactRef == null)
                            {
                                resultItem.Remarks = $"Failed: {account1.Email} / {account1.MasterCustomerId} Contact not found";
                                continue;
                            }

                            // Get Membership Tier History GUID
                            var tierGuid = svc.RetrieveMultiple(new QueryExpression("wkcda_membershiptierhistory")
                            {
                                ColumnSet = new ColumnSet("wkcda_membershiptierhistoryid"),
                                Criteria = { Conditions = { new ConditionExpression("wkcda_membershiphistoryname", ConditionOperator.Equal, account.MemberTierHistoryId) } }
                            }).Entities.FirstOrDefault()?.Id;

                            if (!tierGuid.HasValue)
                            {
                                resultItem.Remarks = $"Failed: Membership Tier History '{account.MemberTierHistoryId}' not found";
                                continue;
                            }
                            // Get Payment Transaction GUID
                            var paymentGuid = svc.RetrieveMultiple(new QueryExpression("wkcda_paymenttransaction")
                            {
                                ColumnSet = new ColumnSet("wkcda_paymenttransactionid"),
                                Criteria = { Conditions = { new ConditionExpression("wkcda_paymenttransaction", ConditionOperator.Equal, account.PaymentHistoryId) } }
                            }).Entities.FirstOrDefault()?.Id;

                            if (!paymentGuid.HasValue)
                            {
                                resultItem.Remarks = $"Failed: Payment Transaction '{account.PaymentHistoryId}' not found";
                                continue;
                            }

                            // Update Membership Group if provided
                            //if (!string.IsNullOrEmpty(account.GroupId))
                            //{
                            //    int stateCodeValue = account.GroupStatus?.ToLower() switch
                            //    {
                            //        "active" => 0,
                            //        "inactive" => 1,
                            //        _ => 0
                            //    };
                            //    var grp = new Entity("wkcda_membershipgroup", Guid.Parse(account.GroupId))
                            //    {
                            //        ["statecode"] = new OptionSetValue(stateCodeValue)
                            //    };
                            //    svc.Update(grp);
                            //}

                            // Get Membership Group Relationship GUID from readable name
                            //var grpRelGuid = svc.RetrieveMultiple(new QueryExpression("wkcda_membershipgrouprelationship")
                            //{
                            //    ColumnSet = new ColumnSet("wkcda_membershipgrouprelationshipid"),
                            //    Criteria = { Conditions = { new ConditionExpression("wkcda_membershipgrouprelationshipname", ConditionOperator.Equal, account.GroupRelationshipId) } }
                            //}).Entities.FirstOrDefault()?.Id;

                            //if (!grpRelGuid.HasValue)
                            //{
                            //    resultItem.Remarks = $"Failed: Membership Group Relationship '{account.GroupRelationshipId}' not found";
                            //    continue;
                            //}

                            // Update Payment Transaction and link to Membership Tier History
                            var paymentTxn = new Entity("wkcda_paymenttransaction", paymentGuid.Value)
                            {
                                ["wkcda_paymenttype"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_paymenttype", account1.PaymentType, _logger) ?? 0),
                                ["wkcda_status"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_status", account1.PaymentStatus, _logger) ?? 0),
                                ["wkcda_transactionno"] = account.PaymentGatewayTransactionNumber,
                                ["wkcda_paymentremarks"] = account.ApprovalCode,
                                ["wkcda_membershiptierhistory"] = new EntityReference("wkcda_membershiptierhistory", tierGuid.Value)
                            };
                            svc.Update(paymentTxn);

                            // Update Membership Tier History and link to Payment Transaction
                            var tierHistory = new Entity("wkcda_membershiptierhistory", tierGuid.Value)
                            {
                                ["wkcda_startdate"] = string.IsNullOrEmpty(account.MembershipStartDate) ? (DateTime?)null : DateTime.Parse(account.MembershipStartDate),
                                ["wkcda_enddate"] = string.IsNullOrEmpty(account.MembershipEndDate) ? (DateTime?)null : DateTime.Parse(account.MembershipEndDate),
                                ["wkcda_membershipstatus"] = account.MembershipStatus,
                                ["wkcda_referenceno"] = account.ReferenceNumber,
                                ["wkcda_paymenttransaction"] = new EntityReference("wkcda_paymenttransaction", paymentGuid.Value),
                                
                            };
                            svc.Update(tierHistory);

                            resultItem.Success = true;
                            resultItem.Remarks = ""; // successful update
                           
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
                    resultItem.MembershipInfo = memResp;
                    response.Add(resultItem);

                }

                return new OkObjectResult(new { AccountProfiles = response });
            }
            catch (JsonException)
            {
                return new OkObjectResult(new ResponseBody_Item { Success = false, Remarks = "Invalid JSON" });
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
                { "MembershipStatus", "wkcda_membershipstatus" },
                { "GroupStatus", "wkcda_status" },
                { "PaymentStatus", "wkcda_paymentstatus" },
                { "PaymentType", "wkcda_paymenttype" }
            };
        }

        private EntityReference? GetContactReference(ServiceClient svc, string masterCustomerId, string email)
        {
            var qe = new QueryExpression("contact") { ColumnSet = new ColumnSet("contactid") };
            if (!string.IsNullOrEmpty(masterCustomerId))
                qe.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, masterCustomerId);
            else if (!string.IsNullOrEmpty(email))
                qe.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, email);
            else
                return null;

            var ec = svc.RetrieveMultiple(qe);
            return ec.Entities.FirstOrDefault() != null ? new EntityReference("contact", ec.Entities[0].Id) : null;
        }

        #region Request/Response Classes
        public class RequestBody
        {
            public required List<AccountProfile> AccountProfiles { get; set; }
        }


        public class AccountProfile
        {
            public string MasterCustomerId { get; set; }
            public string Email { get; set; }
            public string PaymentStatus { get; set; }
            public string PaymentType { get; set; }

            public List<MembershipInfo> MembershipInfo { get; set; }
        }

        public class MembershipInfo
        {
            public string PromoCode { get; set; }
            public string MemberTierHistoryId { get; set; }
            public string MembershipStartDate { get; set; }
            public string MembershipEndDate { get; set; }
            public string MembershipStatus { get; set; }
            public string ReferenceNumber { get; set; }
            public string PaymentGatewayTransactionNumber { get; set; }
            public string ApprovalCode { get; set; }
            public string PaymentHistoryId { get; set; }
        }

        public class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string? Remarks { get; set; }
            public string? MasterCustomerID { get; set; }
            public List<MembershipInfoResponse> MembershipInfo { get; set; }
        }
        public class MembershipInfoResponse
        {
            public string ReferenceNumber { get; set; }
            public string PromoCode { get; set; }
            public string PaymentHistoryId { get; set; }
            public string MemberTierHistoryId { get; set; }
            public string MembershipStartDate { get; set; }
            public string MembershipEndDate { get; set; }
        }
        #endregion
    }
}
