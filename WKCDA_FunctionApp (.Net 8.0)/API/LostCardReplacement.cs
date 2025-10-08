using DataverseModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using WKCDA_FunctionApp__.Net_8._0_.Model;
using static WKCDA_FunctionApp__.Net_8._0_.API.RetrieveEventTransactionHistoryWS;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class LostCardReplacementWS : WS_Base
    {
        public LostCardReplacementWS(ILogger<LostCardReplacementWS> logger) : base(logger) { }

        [Function("LostCardReplacement")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/LostCardReplacement")] HttpRequest req)
        {
            _logger.LogInformation("APIName: LostCardReplacement");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.AccountProfile == null)
                    return new OkObjectResult(new ResponseBody { Success = false, Remarks = "Invalid request body" });

                var accountProfile = requestBody.AccountProfile;
                var response = await HandleLogicAsync(svc, accountProfile);

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

        private async Task<ResponseBody> HandleLogicAsync(ServiceClient svc, AccountProfile accountProfile)
        {
            var response = new ResponseBody();

            if (string.IsNullOrWhiteSpace(accountProfile.MemberTierHistoryId))
            {
                return ReturnError(response, "MemberTierHistoryId is Blank!");
            }

            if (string.IsNullOrWhiteSpace(accountProfile.CardNumber))
            {
                return ReturnError(response, "CardNumber is Blank!");
            }

            accountProfile.CardNumber = accountProfile.CardNumber.ToUpper();

            try
            {
                var originalCard = await GetMembershipCardAsync(svc, accountProfile.CardNumber, accountProfile.MemberTierHistoryId);
                if (originalCard == null)
                {
                    return ReturnError(response, $"Could not found the Membership Card record with the given combination of 'CardNumber={accountProfile.CardNumber}' and 'MemberTierHistoryId={accountProfile.MemberTierHistoryId}' in Dataverse!");
                }

                var isCardLost = originalCard.GetAttributeValue<bool>("wkcda_iscardlost");
                if (isCardLost)
                {
                    return ReturnError(response, $"MembershipCard {accountProfile.CardNumber} is already a lost card!");
                }

                var membershipTierHistoryRef = originalCard.GetAttributeValue<EntityReference>("wkcda_membershiptierhistory");
                var membershipTierHistory = await GetMembershipTierHistoryAsync(svc, membershipTierHistoryRef.Id);

                if (membershipTierHistory == null)
                {
                    return ReturnError(response, "Related Membership Tier History not found!");
                }

                var memberRef = membershipTierHistory.GetAttributeValue<EntityReference>("wkcda_member");
                _logger.LogInformation($"Found {memberRef.Name} wkcda_member records");
                var memberAccount = await GetMemberAccountAsync(svc, memberRef.Id);

                if (memberAccount == null)
                {
                    return ReturnError(response, "Related member account not found!");
                }

                await MarkCardAsLostAsync(svc, originalCard.Id, accountProfile.CardLostReportChannel);

                var newCard = await GetNewMembershipCardAsync(svc, accountProfile.MemberTierHistoryId);
                if (newCard == null)
                {
                    return ReturnError(response, "Unexpected error: New card not created!");
                }

                response.Success = true;
                response.MasterCustomerID = memberAccount.GetAttributeValue<string>("wkcda_mastercustomerid");
                response.MemberTierHistoryId = membershipTierHistory.Id.ToString();
                response.MembershipStartDate = membershipTierHistory.GetAttributeValue<DateTime?>("wkcda_startdate")?.ToString("yyyy-MM-dd");
                response.MembershipEndDate = membershipTierHistory.GetAttributeValue<DateTime?>("wkcda_enddate")?.ToString("yyyy-MM-dd");
                var membershipTierRef = membershipTierHistory.GetAttributeValue<EntityReference>("wkcda_membershiptier");
                var membershipTier = await GetMembershipTierAsync(svc, membershipTierRef.Id);
                response.MemberTierName = membershipTierHistory.GetAttributeValue<AliasedValue>("mt.wkcda_membershiptiername")?.Value as string;
                response.BusinessUnit = membershipTierHistory.GetAttributeValue<AliasedValue>("mt.wkcda_businessunit")?.Value as string;

                response.MemberID = newCard.GetAttributeValue<string>("wkcda_memberid");
                response.CardNumber = newCard.GetAttributeValue<string>("wkcda_cardnumber");

                if (string.IsNullOrWhiteSpace(response.MemberID))
                {
                    return ReturnError(response, "Unexpected error: Member Id not generated!");
                }

                var paymentTransactionId = await CreatePaymentTransactionAsync(svc, accountProfile, response.MemberID);
                if (string.IsNullOrWhiteSpace(paymentTransactionId))
                {
                    return ReturnError(response, "Error encountered while creating Payment Transaction!");
                }


                response.PaymentHistoryId = paymentTransactionId;

                return response;
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception($"unauthorized: {ex.Message}");
                }
                return ReturnError(response, $"Error processing request: {ex.Message}");
            }
        }

        private async Task<Entity> GetMembershipCardAsync(ServiceClient svc, string cardNumber, string memberTierHistoryId)
        {
            var query = new QueryExpression("wkcda_membershipcard")
            {
                ColumnSet = new ColumnSet("wkcda_membershipcardid", "wkcda_iscardlost", "wkcda_cardnumber",
                    "wkcda_membershiptierhistory", "wkcda_cardlostreportchannel"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wkcda_cardnumber", ConditionOperator.Equal, cardNumber),
                        new ConditionExpression("wkcda_membershiptierhistory", ConditionOperator.Equal, new Guid(memberTierHistoryId))
                    }
                }
            };

            var result = await svc.RetrieveMultipleAsync(query);
            return result.Entities.Count > 0 ? result.Entities[0] : null;
        }

        private async Task<Entity> GetMembershipTierHistoryAsync(ServiceClient svc, Guid membershipTierHistoryId)
        {
            var query = new QueryExpression("wkcda_membershiptierhistory")
            {
                ColumnSet = new ColumnSet("wkcda_membershiptierhistoryid", "wkcda_member", "wkcda_membershiptier",
                    "wkcda_startdate", "wkcda_enddate"),
                Criteria = new FilterExpression
                {
                    Conditions =
            {
                new ConditionExpression("wkcda_membershiptierhistoryid", ConditionOperator.Equal, membershipTierHistoryId)
            }
                }
            };

            var membershipTierLink = query.AddLink("wkcda_membershiptier", "wkcda_membershiptier", "wkcda_membershiptierid");
            membershipTierLink.Columns = new ColumnSet("wkcda_membershiptiername", "wkcda_businessunit");
            membershipTierLink.EntityAlias = "mt";

            var result = await svc.RetrieveMultipleAsync(query);

            if (result.Entities.Count > 0)
            {
                var entity = result.Entities[0];
                _logger.LogInformation($"Found MembershipTierHistory: StartDate={entity.GetAttributeValue<DateTime?>("wkcda_startdate")}, EndDate={entity.GetAttributeValue<DateTime?>("wkcda_enddate")}");
                _logger.LogInformation($"Available attributes: {string.Join(", ", entity.Attributes.Keys)}");
            }

            return result.Entities.Count > 0 ? result.Entities[0] : null;
        }

        private async Task<Entity> GetMemberAccountAsync(ServiceClient svc, Guid accountId)
        {
            var query = new QueryExpression("contact")
            {
                ColumnSet = new ColumnSet("contactid", "wkcda_mastercustomerid"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("contactid", ConditionOperator.Equal, accountId)
                    }
                }
            };

            var result = await svc.RetrieveMultipleAsync(query);
            return result.Entities.Count > 0 ? result.Entities[0] : null;
        }

        private async Task<Entity> GetMembershipTierAsync(ServiceClient svc, Guid membershipTierId)
        {
            var query = new QueryExpression("wkcda_membershiptier")
            {
                ColumnSet = new ColumnSet("wkcda_membershiptierid", "wkcda_membershiptiername", "wkcda_businessunit"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wkcda_membershiptierid", ConditionOperator.Equal, membershipTierId)
                    }
                }
            };

            var result = await svc.RetrieveMultipleAsync(query);
            return result.Entities.Count > 0 ? result.Entities[0] : null;
        }

        private async Task MarkCardAsLostAsync(ServiceClient svc, Guid cardId, string cardLostReportChannel)
        {
            var cardToUpdate = new Entity("wkcda_membershipcard", cardId);
            cardToUpdate["wkcda_iscardlost"] = true;

            if (!string.IsNullOrWhiteSpace(cardLostReportChannel))
            {
                cardToUpdate["wkcda_cardlostreportchannel"] = cardLostReportChannel;
            }

            await svc.UpdateAsync(cardToUpdate);
        }

        private async Task<Entity> GetNewMembershipCardAsync(ServiceClient svc, string memberTierHistoryId)
        {
            var query = new QueryExpression("wkcda_membershipcard")
            {
                ColumnSet = new ColumnSet("wkcda_membershipcardid", "wkcda_memberid", "wkcda_cardnumber", "wkcda_iscardlost", "wkcda_mthmemberid"),
                Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression("wkcda_membershiptierhistory", ConditionOperator.Equal, new Guid(memberTierHistoryId))
                       // new ConditionExpression("wkcda_iscardlost", ConditionOperator.Equal, false)
                    }
                },
                Orders =
                {
                    new OrderExpression("createdon", OrderType.Descending)
                }
            };

            var result = await svc.RetrieveMultipleAsync(query);
            _logger.LogInformation($"Found {result.Entities.Count} result records");

            return result.Entities.Count > 0 ? result.Entities[0] : null;
        }

        private async Task<string> CreatePaymentTransactionAsync(ServiceClient svc, AccountProfile accountProfile, string memberId)
        {
            try
            {
                var paymentTransaction = new Entity("wkcda_paymenttransaction");

                paymentTransaction["wkcda_paymentremarks"] = $"Lost Card Replacement - {accountProfile.CardNumber}";
                paymentTransaction["wkcda_totalamount"] = new Money(accountProfile.TotalAmount);
                paymentTransaction["wkcda_paymentamount"] = new Money(accountProfile.PaymentAmount);
                paymentTransaction["wkcda_discountamount"] = new Money(accountProfile.DiscountAmount);

                paymentTransaction["wkcda_transactiontype"] = new OptionSetValue(
                    GetOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_transactiontype", "Lost Card") ?? 100000003);

                paymentTransaction["wkcda_paymenttype"] = new OptionSetValue(
                    GetOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_paymenttype", accountProfile.PaymentType) ??
                    GetDefaultPaymentTypeValue(accountProfile.PaymentType));

                paymentTransaction["wkcda_saleschannel"] = new OptionSetValue(
                    GetOptionSetValue(svc, "wkcda_paymenttransaction", "wkcda_saleschannel", accountProfile.SalesChannel) ??
                    GetDefaultSalesChannelValue(accountProfile.SalesChannel));

                paymentTransaction["wkcda_transactionno"] = accountProfile.PaymentGatewayTransactionNumber;
                // paymentTransaction["wkcda_paidby"] = accountProfile.PaidBy;
                if (!string.IsNullOrWhiteSpace(accountProfile.PaidBy))
                {
                    var paidByContact = await GetContactByEmailAsync(svc, accountProfile.PaidBy);
                    if (paidByContact != null)
                    {
                        paymentTransaction["wkcda_paidby"] = paidByContact.ToEntityReference();
                    }
                    else
                    {
                        _logger.LogWarning($"Contact not found for PaidBy email: {accountProfile.PaidBy}");
                    }
                }

                // paymentTransaction["wkcda_member"] = new EntityReference("contact", new Guid(memberId));

                if (DateTime.TryParse(accountProfile.PaymemtDate, out DateTime paymentDate))
                {
                    paymentTransaction["wkcda_paymentdate"] = paymentDate;
                }

                var paymentTransactionId = await svc.CreateAsync(paymentTransaction);
                _logger.LogInformation($"Created payment transaction with ID: {paymentTransactionId}");
                return paymentTransactionId.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating payment transaction");
                return null;
            }
        }

        private async Task<Entity> GetContactByEmailAsync(ServiceClient svc, string email)
        {
            try
            {
                var query = new QueryExpression("contact")
                {
                    ColumnSet = new ColumnSet("contactid", "emailaddress1"),
                    Criteria = new FilterExpression
                    {
                        Conditions =
                {
                    new ConditionExpression("emailaddress1", ConditionOperator.Equal, email)
                }
                    },
                    TopCount = 1
                };

                var result = await svc.RetrieveMultipleAsync(query);
                return result.Entities.Count > 0 ? result.Entities[0] : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error finding contact by email: {email}");
                return null;
            }
        }

        private int? GetOptionSetValue(ServiceClient svc, string entityName, string attributeName, string optionSetLabel)
        {
            try
            {
                // We need to use RetrieveAttributeRequest to get option set metadata
                var attributeRequest = new RetrieveAttributeRequest
                {
                    EntityLogicalName = entityName,
                    LogicalName = attributeName,
                    RetrieveAsIfPublished = true
                };

                var attributeResponse = (RetrieveAttributeResponse)svc.Execute(attributeRequest);
                var attributeMetadata = (EnumAttributeMetadata)attributeResponse.AttributeMetadata;

                foreach (var option in attributeMetadata.OptionSet.Options)
                {
                    if (option.Label.UserLocalizedLabel != null &&
                        option.Label.UserLocalizedLabel.Label.Equals(optionSetLabel, StringComparison.OrdinalIgnoreCase))
                    {
                        return option.Value;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        private int GetDefaultPaymentTypeValue(string paymentType)
        {
            var defaultMappings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        { "Visa", 100000001 },
        { "MasterCard", 100000002 },
        { "American Express", 100000003 },
        { "Cash", 100000004 },
        { "Credit Card", 100000005 }
    };
            return defaultMappings.TryGetValue(paymentType, out int value) ? value : 100000001;
        }

        private int GetDefaultSalesChannelValue(string salesChannel)
        {
            var defaultMappings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        { "Onsite", 100000001 },
        { "Online", 100000002 },
        { "Phone", 100000003 },
        { "Mobile App", 100000004 }
    };
            return defaultMappings.TryGetValue(salesChannel, out int value) ? value : 100000001;
        }
   
        private ResponseBody ReturnError(ResponseBody response, string remarks)
        {
            response.Success = false;
            response.Remarks = remarks;
            _logger.LogError(remarks);
            return response;
        }

        #endregion

        #region Request/Response Classes

        public class RequestBody
        {
            public AccountProfile AccountProfile { get; set; }
        }

        public class AccountProfile
        {
            public string MemberTierHistoryId { get; set; }
            public string CardNumber { get; set; }
            public string CardLostReportChannel { get; set; }
            public string ConfirmationEmailRecipient { get; set; }
            public decimal TotalAmount { get; set; }
            public decimal PaymentAmount { get; set; }
            public decimal DiscountAmount { get; set; }
            public string PaymentType { get; set; }
            public string SalesChannel { get; set; }
            public string PaymemtDate { get; set; }
            public string PaidBy { get; set; }
            public string PaymentGatewayTransactionNumber { get; set; }
            public string ApprovalCode { get; set; }
        }

        public class ResponseBody
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public string MasterCustomerID { get; set; }
            public string PaymentHistoryId { get; set; }
            public string MemberTierHistoryId { get; set; }
            public string MembershipStartDate { get; set; }
            public string MembershipEndDate { get; set; }
            public string MemberTierName { get; set; }
            public string MemberID { get; set; }
            public string CardNumber { get; set; }
            public string BusinessUnit { get; set; }
        }

        #endregion
    }
}