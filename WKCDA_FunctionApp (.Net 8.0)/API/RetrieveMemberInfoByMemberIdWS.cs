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
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class RetrieveMemberInfoByMemberIdWS : WS_Base
    {
        public RetrieveMemberInfoByMemberIdWS(ILogger<RetrieveMemberInfoByMemberIdWS> logger) : base(logger) { }

        [Function("RetrieveMemberInfoByMemberIdWS")]
        public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/RetrieveMemberInfoByMemberIdWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: RetrieveMemberInfoByMemberIdWS");
            _logger.LogInformation("startTime: " + DateTime.Now);
            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.Customers == null)
                    return new OkObjectResult(new ResponseBody { Success = false, Remarks = "Invalid request body" });

                var customer = requestBody.Customers.FirstOrDefault();
                var response = new ResponseBody();

                if (string.IsNullOrWhiteSpace(customer?.MemberId))
                {
                    response.Success = false;
                    response.Remarks = "MemberId is required.";
                    return new ObjectResult(new { error = response })
                    {
                        StatusCode = StatusCodes.Status400BadRequest
                    };
                }
                // Validate and fetch contact
                var contact = GetContactByMemberId(svc, customer.MemberId);
                if (contact == null)
                {
                    var errorResponse = new
                    {
                        Success = false,
                        Remarks = $"{customer.MemberId}: Customer does not exist.",
                        MasterCustomerID = (string)null
                    };

                    return new OkObjectResult(new[] { errorResponse });
                }


                // Map to response
                response.Success = true;
                response.Customer = contact.GetAttributeValue<string>("fullname");
                response.MemberId = contact.GetAttributeValue<string>("wkcda_memberid");
                response.Email = contact.GetAttributeValue<string>("emailaddress1");
                response.MasterCustomerID = contact.GetAttributeValue<string>("wkcda_mastercustomerid");
                response.MembershipCreateDate = contact.GetAttributeValue<DateTime?>("wkcda_membershipcreateddate")?.ToString("yyyy-MM-dd");
                response.MembershipRemarks = contact.GetAttributeValue<string>("wkcda_remarksmembership");
                response.PaidMembership = contact.GetAttributeValue<bool?>("wkcda_paidmembership") ?? false;
                response.LoyaltyPoint = contact.GetAttributeValue<int?>("wkcda_loyaltypointbalance");
                response.MembershipTierPoint = contact.GetAttributeValue<int?>("wkcda_membershiptierpointbalance");
                response.VIPReference = contact.GetAttributeValue<string>("wkcda_vipreference");

                // Get membership tier history
                response.MembershipDetail = GetMembershipTierHistory(svc, contact.Id);

                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new ResponseBody { Success = false, Remarks = "Invalid JSON" });
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
                    _logger.LogError(ex, "Error in RetrieveMemberInfoByMemberIdWS: {Message}", ex.Message);
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

        private Entity GetContactByMemberId(ServiceClient svc, string memberId)
        {
            var query = new QueryExpression("contact")
            {
                ColumnSet = new ColumnSet(
                    "fullname", "wkcda_memberid", "emailaddress1", "wkcda_mastercustomerid",
                    "wkcda_membershipcreateddate", "wkcda_remarksmembership", "wkcda_paidmembership",
                    "wkcda_loyaltypointbalance", "wkcda_membershiptierpointbalance", "wkcda_vipreference"
                ),
                Criteria = new FilterExpression
                {
                    Conditions = { new ConditionExpression("wkcda_memberid", ConditionOperator.Equal, memberId) }
                }
            };

            var result = svc.RetrieveMultiple(query);
            return result.Entities.FirstOrDefault();
        }

        private List<MembershipDetail> GetMembershipTierHistory(ServiceClient svc, Guid contactId)
        {
            var details = new List<MembershipDetail>();

            var query = new QueryExpression("wkcda_membershiptierhistory")
            {
                ColumnSet = new ColumnSet(
                    "wkcda_startdate", "wkcda_enddate", "wkcda_membershipstatus",
                    "wkcda_firsttimeupgrade", "wkcda_remarks", "wkcda_firsttimegiftstatus",
                    "wkcda_paidmembership"
                ),
                Criteria = new FilterExpression
                {
                    Conditions = { new ConditionExpression("wkcda_member", ConditionOperator.Equal, contactId) }
                },
                LinkEntities =
                {
                    new LinkEntity("wkcda_membershiptierhistory", "wkcda_membershiptier", "wkcda_membershiptier", "wkcda_membershiptierid", JoinOperator.Inner)
                    {
                        Columns = new ColumnSet("wkcda_membershiptiername"),
                        EntityAlias = "tier"
                    }
                }
            };

            var result = svc.RetrieveMultiple(query);
            foreach (var entity in result.Entities)
            {
                details.Add(new MembershipDetail
                {
                    StartDate = entity.GetAttributeValue<DateTime?>("wkcda_startdate")?.ToString("yyyy-MM-dd"),
                    EndDate = entity.GetAttributeValue<DateTime?>("wkcda_enddate")?.ToString("yyyy-MM-dd"),
                    Status = entity.GetAttributeValue<string>("wkcda_membershipstatus"),
                    FirstTimeUpgrade = entity.GetAttributeValue<bool?>("wkcda_firsttimeupgrade") ?? false,
                    Remarks = entity.GetAttributeValue<string>("wkcda_remarks"),
                    MembershipTier = entity.GetAttributeValue<AliasedValue>("tier.wkcda_membershiptiername")?.Value?.ToString(),
                    FirstTimeGiftStatus = entity.GetAttributeValue<string>("wkcda_firsttimegiftstatus"),
                    PaidMembership = entity.GetAttributeValue<bool?>("wkcda_paidmembership") ?? false,
                    Renewal = false // You can populate from renewal entity if needed
                });
            }

            return details;
        }
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
            public List<Customer> Customers { get; set; }
        }
        public class Customer
        {
            public string MemberId { get; set; }
        }
        public class ResponseBody
        {
            public string VIPReference { get; set; }
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public bool PaidMembership { get; set; }
            public int? MembershipTierPoint { get; set; }
            public string MembershipRemarks { get; set; }
            public string MembershipCreateDate { get; set; }
            public string MemberId { get; set; }
            public string MasterCustomerID { get; set; }
            public int? LoyaltyPoint { get; set; }
            public string Email { get; set; }
            public string Customer { get; set; }
            public List<MembershipDetail> MembershipDetail { get; set; }
        }
        public class MembershipDetail
        {
            public string Status { get; set; }
            public string StartDate { get; set; }
            public string Remarks { get; set; }
            public bool PaidMembership { get; set; }
            public string MembershipTier { get; set; }
            public bool FirstTimeUpgrade { get; set; }
            public string FirstTimeGiftStatus { get; set; }
            public string EndDate { get; set; }
            public bool Renewal { get; set; }
        }
        #endregion
    }
}
