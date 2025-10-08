using DataverseModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class PaidMembershipPurchaseAfterPayment : WS_Base
    {
        public PaidMembershipPurchaseAfterPayment(ILogger<PaidMembershipPurchaseAfterPayment> logger) : base(logger) { }

        [Function("PaidMembershipPurchaseAfterPayment")]
        public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/PaidMembershipPurchaseAfterPayment")] HttpRequest req)
        {
            _logger.LogInformation("APIName: PaidMembershipPurchaseAfterPayment");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.AccountProfiles == null || !requestBody.AccountProfiles.Any())
                    return new OkObjectResult(new ResponseBody_Item { Success = false, Remarks = "Invalid request body" });

                var response = new List<ResponseBody_Item>();

                foreach (var account in requestBody.AccountProfiles)
                {
                    var resultItem = new ResponseBody_Item
                    {
                        AccountProfile = account,
                        Success = false,
                        Remarks = null
                    };
                    response.Add(resultItem);
                    if (string.IsNullOrWhiteSpace(account.ReferenceNumber))
                    {
                        resultItem.Remarks = $"Failed: {account.Email} Reference Number is missing";
                        continue;
                    }
                    // Check required fields
                    if (string.IsNullOrWhiteSpace(account.PaymentHistoryId))
                    {
                        resultItem.Remarks = $"Failed: {account.Email} PaymentHistoryId Not found";
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(account.MemberTierHistoryId))
                    {
                        resultItem.Remarks = $"Failed: {account.Email} MemberTierHistoryId Not found";
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(account.GroupRelationshipId))
                    {
                        resultItem.Remarks = $"Failed: {account.Email} GroupRelationshipId Not found";
                        continue;
                    }

                    try
                    {
                        // Get Contact
                        var contactRef = GetContactReference(svc, account.MasterCustomerId, account.Email);
                        if (contactRef == null)
                        {
                            resultItem.Remarks = $"Failed: {account.Email} / {account.MasterCustomerId} Contact not found";
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
                        // Validate Reference Number exists in CRM
                        var refCheck = svc.RetrieveMultiple(new QueryExpression("wkcda_membershiptierhistory")
                        {
                            ColumnSet = new ColumnSet("wkcda_membershiptierhistoryid"),
                            Criteria = { Conditions = { new ConditionExpression("wkcda_referenceno", ConditionOperator.Equal, account.ReferenceNumber) } }
                        }).Entities.FirstOrDefault();

                        if (refCheck == null)
                        {
                            resultItem.Remarks = $"Failed: Reference Number '{account.ReferenceNumber}' not found in CRM";
                            continue; // skip update
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
                        if (!string.IsNullOrEmpty(account.GroupId))
                        {
                            int stateCodeValue = account.GroupStatus?.ToLower() switch
                            {
                                "active" => 0,
                                "inactive" => 1,
                                _ => 0
                            };
                            var grp = new Entity("wkcda_membershipgroup", Guid.Parse(account.GroupId))
                            {
                                ["statecode"] = new OptionSetValue(stateCodeValue)
                            };
                            svc.Update(grp);
                        }

                        // Get Membership Group Relationship GUID from readable name
                        var grpRelGuid = svc.RetrieveMultiple(new QueryExpression("wkcda_membershipgrouprelationship")
                        {
                            ColumnSet = new ColumnSet("wkcda_membershipgrouprelationshipid"),
                            Criteria = { Conditions = { new ConditionExpression("wkcda_membershipgrouprelationshipname", ConditionOperator.Equal, account.GroupRelationshipId) } }
                        }).Entities.FirstOrDefault()?.Id;

                        if (!grpRelGuid.HasValue)
                        {
                            resultItem.Remarks = $"Failed: Membership Group Relationship '{account.GroupRelationshipId}' not found";
                            continue;
                        }

                        // Update Payment Transaction and link to Membership Tier History
                        var paymentTxn = new Entity("wkcda_paymenttransaction", paymentGuid.Value)
                        {
                            ["wkcda_paymenttype"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_paymenttype", account.PaymentType, _logger) ?? 0),
                            ["wkcda_status"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_status", account.PaymentStatus, _logger) ?? 0),
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
                            ["wkcda_paymenttransaction"] = new EntityReference("wkcda_paymenttransaction", paymentGuid.Value),
                            ["wkcda_membershipgrouprelationship"] = new EntityReference("wkcda_membershipgrouprelationship", grpRelGuid.Value)
                        };
                        svc.Update(tierHistory);

                        resultItem.Success = true;
                        resultItem.Remarks = "";
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
            public required List<AccountProfileInput> AccountProfiles { get; set; }
        }

        public class AccountProfileInput
        {
            public string MasterCustomerId { get; set; }
            public string Email { get; set; }
            public string MemberTierHistoryId { get; set; }
            public string MembershipStartDate { get; set; }
            public string MembershipEndDate { get; set; }
            public string MembershipStatus { get; set; }
            public string ReferenceNumber { get; set; }
            public string PaymentGatewayTransactionNumber { get; set; }
            public string ApprovalCode { get; set; }
            public string PromoCode { get; set; }
            public string PaymentHistoryId { get; set; }
            public string PaymentStatus { get; set; }
            public string GroupRelationshipId { get; set; }
            public string GroupId { get; set; }
            public string GroupStatus { get; set; }
            public string PaymentType { get; set; }
        }

        public class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string? Remarks { get; set; }
            public AccountProfileInput AccountProfile { get; set; }
        }
        #endregion
    }
}
