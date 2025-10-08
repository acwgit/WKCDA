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
using System.Collections.Generic;
using Microsoft.Xrm.Sdk.PluginTelemetry;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class TicketingReconciliationRealtimeWS : WS_Base
    {
        public TicketingReconciliationRealtimeWS(ILogger<TicketingReconciliationRealtimeWS> logger) : base(logger) { }

        [Function("TicketingReconciliationRealtimeWS")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/TicketingReconciliationRealtimeWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: TicketingReconciliationRealtimeWS");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();
                var ecCountry = GetCountry(svc);
                if (requestBody?.ticketingRecons == null || !requestBody.ticketingRecons.Any())
                    return new OkObjectResult(new ResponseBody_Item { Success = false, Remarks = "Invalid request body" });

                var response = new List<ResponseBody_Item>();

                foreach (var recon in requestBody.ticketingRecons)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(recon.TicketingTxnHeader.MasterCustomerID)
                            && string.IsNullOrWhiteSpace(recon.TicketingTxnHeader.CustomerEmail))
                        {
                            response.Add(new ResponseBody_Item
                            {
                                Success = false,
                                Remarks = "Please provide customer ID or Email."
                            });
                            continue;
                        }
                        var headerEntity = new Entity("wkcda_ticketingtransaction");
                        headerEntity["wkcda_transactionid"] = recon.TicketingTxnHeader.TransactionID;
                        headerEntity["wkcda_saleschannel"] = recon.TicketingTxnHeader.SalesChannel;
                        headerEntity["wkcda_returnreason"] = recon.TicketingTxnHeader.ReturnReason;
                        headerEntity["wkcda_customerlastname"] = recon.TicketingTxnHeader.CustomerLastName;
                        headerEntity["wkcda_sisticpatronid"] = recon.TicketingTxnHeader.SISTICPatronID;
                        headerEntity["wkcda_transactiontype"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_ticketingtransaction", "wkcda_transactiontype", recon.TicketingTxnHeader.TransactionType, _logger) ?? 0);
                        headerEntity["wkcda_noofticketspurchased"] = recon.TicketingTxnHeader.NoOfTicketsPurchased;
                        headerEntity["wkcda_guestpurchase"] = recon.TicketingTxnHeader.GuestPurchase;
                        headerEntity["wkcda_reprintreason"] = recon.TicketingTxnHeader.ReprintReason;
                        headerEntity["wkcda_posterminalseller"] = recon.TicketingTxnHeader.POSTerminalSeller;
                        headerEntity["wkcda_totalamount"] = new Money(recon.TicketingTxnHeader.TotalAmount);
                        headerEntity["wkcda_transactiondatetime"] = recon.TicketingTxnHeader.TransactionDateTime;
                        var contactRef = GetContactReference(svc, recon.TicketingTxnHeader.MasterCustomerID, recon.TicketingTxnHeader.CustomerEmail);
                        if (contactRef != null) headerEntity["wkcda_member"] = contactRef;
                        headerEntity["wkcda_mastercustomerid"] = recon.TicketingTxnHeader.MasterCustomerID;
                        headerEntity["wkcda_customeremail"] = recon.TicketingTxnHeader.CustomerEmail;
                        var headerId = svc.Create(headerEntity);
                        if (recon.TicketingTransactions != null)
                        {
                            foreach (var tr in recon.TicketingTransactions)
                            {
                                var trEntity = new Entity("wkcda_perticketbreakdown");
                                trEntity["wkcda_ticketingtransactionheader"] = new EntityReference("wkcda_ticketingtransaction", headerId);
                                var header = svc.Retrieve("wkcda_ticketingtransaction", headerId, new ColumnSet("wkcda_transactionid"));
                                if (header != null && header.Contains("wkcda_transactionid"))
                                {
                                    trEntity["wkcda_transactionid"] = header.GetAttributeValue<string>("wkcda_transactionid");
                                }
                                trEntity["wkcda_ticketingticketid"] = tr.TicketingTicketID;
                                trEntity["wkcda_eventcode"] = tr.EventCode;
                                trEntity["wkcda_ticketingeventid"] = tr.TicketingEventID;
                                trEntity["wkcda_pricecategoryname"] = tr.PriceCategoryName;
                                trEntity["wkcda_priceclassname"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_perticketbreakdown", "wkcda_priceclassname", tr.PriceClassName, _logger) ?? 0);
                                trEntity["wkcda_priceclasscode"] = tr.PriceClassCode;
                                trEntity["wkcda_seatattribute"] = tr.SeatAttribute;
                                trEntity["wkcda_seatnorow"] = tr.SeatNoRow;
                                trEntity["wkcda_seatnoseat"] = tr.SeatNoSeat;
                                trEntity["wkcda_seatnosection"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_perticketbreakdown", "wkcda_seatnosection", tr.SeatNoSection, _logger) ?? 0);
                                trEntity["wkcda_seattype"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_perticketbreakdown", "wkcda_seattype", tr.SeatType, _logger) ?? 0);
                                trEntity["wkcda_level"] = tr.Level;
                                trEntity["wkcda_tickettype"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_perticketbreakdown", "wkcda_tickettype", tr.TicketType, _logger) ?? 0);
                                var deliveryMethodValue = CRMEntityHelper.getOptionSetValue(svc, "wkcda_perticketbreakdown", "wkcda_deliverymethod", tr.DeliveryMethod, _logger);
                                if (deliveryMethodValue.HasValue)
                                trEntity["wkcda_deliverymethod"] = new OptionSetValue(deliveryMethodValue.Value);
                                trEntity["wkcda_deliveryaddress1"] = tr.DeliveryAddress1;
                                trEntity["wkcda_deliveryaddress2"] = tr.DeliveryAddress2;
                                trEntity["wkcda_deliveryaddress3"] = tr.DeliveryAddress3;
                                trEntity["wkcda_deliveryaddresscountry"] = GetCountryRef(ecCountry, tr.DeliveryAddressCountry);
                                trEntity["wkcda_basepricegrossunitprice"] = new Money(tr.BasePriceGrossUnitPrice);
                                trEntity["wkcda_discountamount"] = new Money(tr.DiscountAmount);
                                trEntity["wkcda_payticketamount"] = new Money(tr.PayTicketAmount);
                                var trId = svc.Create(trEntity);
                                if (recon.TicketingTicLvFees != null && recon.TicketingTicLvFees.Any())
                                {
                                    foreach (var fee in recon.TicketingTicLvFees.Where(f => f.TicketingTicketID?.ToString() == tr.TicketingTicketID?.ToString()))
                                    {
                                        try
                                        {
                                            var feeEntity = new Entity("wkcda_perticketadditionalfees");
                                            feeEntity["wkcda_ticketingtransaction"] = new EntityReference("wkcda_perticketbreakdown", trId);
                                            feeEntity["wkcda_transactionid"] = recon.TicketingTxnHeader.TransactionID;
                                            feeEntity["wkcda_ticketingticketid"] = tr.TicketingTicketID;
                                            feeEntity["wkcda_ticketoutsidechargefeename"] = fee.TicketOutsideChargeFeeName;
                                            feeEntity["wkcda_ticketoutsidechargefeeamount"] = new Money(fee.TicketOutsideChargeFeeAmount);

                                            svc.Create(feeEntity);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, $"Failed to create additional fee for ticket {tr.TicketingTicketID}, fee {fee.TicketOutsideChargeFeeName}");
                                        }
                                    }
                                }

                            }
                        }
                        if (recon.TicketingTxnHeaderLvFees != null && recon.TicketingTxnHeaderLvFees.Any())
                        {
                            foreach (var hfee in recon.TicketingTxnHeaderLvFees)
                            {
                                var addEntity = new Entity("wkcda_ticketingtransactionadditionalfees");
                                addEntity["wkcda_ticketingtransactionheader"] = new EntityReference("wkcda_ticketingtransaction", headerId);
                                var headerRecord = svc.Retrieve("wkcda_ticketingtransaction", headerId, new ColumnSet("wkcda_transactionid"));
                                if (headerRecord != null && headerRecord.Contains("wkcda_transactionid"))
                                {
                                    addEntity["wkcda_transactionid"] = headerRecord.GetAttributeValue<string>("wkcda_transactionid");
                                }
                                addEntity["wkcda_transactionoutsidechargefeename"] = hfee.TransactionOutsideChargeFeeName;
                                addEntity["wkcda_transactionoutsidechargefeeamount"] = new Money(hfee.TransactionOutsideChargeFeeAmount);

                                svc.Create(addEntity);
                            }
                        }
                        if (recon.TicketingPayments != null)
                        {
                            foreach (var pay in recon.TicketingPayments)
                            {
                                var payEntity = new Entity("wkcda_ticketingpayment");
                                payEntity["wkcda_ticketingtransactionheader"] = new EntityReference("wkcda_ticketingtransaction", headerId);
                                var headerRecord = svc.Retrieve("wkcda_ticketingtransaction", headerId, new ColumnSet("wkcda_transactionid"));
                                if (headerRecord != null && headerRecord.Contains("wkcda_transactionid"))
                                {
                                    payEntity["wkcda_transactionid"] = headerRecord.GetAttributeValue<string>("wkcda_transactionid");
                                }
                                payEntity["wkcda_paymentmethod"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_ticketingpayment", "wkcda_paymentmethod", pay.PaymentMethod, _logger) ?? 0);
                                payEntity["wkcda_paymentamount"] = new Money(pay.PaymentAmount);

                                svc.Create(payEntity);
                            }
                        }
                        response.Add(new ResponseBody_Item { Success = true, Remarks = null });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing ticketing reconciliation");
                        response.Add(new ResponseBody_Item { Success = false, Remarks = ex.Message });
                    }
                }
                return new OkObjectResult(response);
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
                { "", "" },
            };
        }
        private EntityReference? GetContactReference(ServiceClient svc, string custId, string email)
        {
            var qe = new QueryExpression("contact") { ColumnSet = new ColumnSet("contactid") };
            if (!string.IsNullOrEmpty(custId))
                qe.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, custId);
            else if (!string.IsNullOrEmpty(email))
                qe.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, email);
            else
                return null;

            var ec = svc.RetrieveMultiple(qe);
            return ec.Entities.FirstOrDefault() != null ? new EntityReference("contact", ec.Entities[0].Id) : null;
        }
        private EntityCollection GetCountry(ServiceClient svc)
        {
            var qe = new QueryExpression("wkcda_country");
            qe.ColumnSet.AddColumns("wkcda_countryname");

            return svc.RetrieveMultiple(qe);
        }
        private EntityReference GetCountryRef(EntityCollection ec, string name)
        {
            return ec.Entities.FirstOrDefault(o => o.GetAttributeValue<string>("wkcda_countryname") == name)?.ToEntityReference();
        }
        #region Request/Response Classes
        public class RequestBody
        {
            public required List<TicketingRecon> ticketingRecons { get; set; }
        }

        public class TicketingRecon
        {
            public TicketingTxnHeader TicketingTxnHeader { get; set; }
            public List<TicketingTransaction> TicketingTransactions { get; set; }
            public List<TicketingTicLvFee> TicketingTicLvFees { get; set; }
            public List<TicketingTxnHeaderLvFee> TicketingTxnHeaderLvFees { get; set; }
            public List<TicketingPayment> TicketingPayments { get; set; }
        }

        public class TicketingTxnHeader
        {
            public string TransactionID { get; set; }
            public string MasterCustomerID { get; set; }
            public string SalesChannel { get; set; }
            public string ReturnReason { get; set; }
            public string CustomerLastName { get; set; }
            public string SISTICPatronID { get; set; }
            public string TransactionType { get; set; }
            public double NoOfTicketsPurchased { get; set; }
            public bool GuestPurchase { get; set; }
            public string CustomerEmail { get; set; }
            public string ReprintReason { get; set; }
            public string POSTerminalSeller { get; set; }
            public decimal TotalAmount { get; set; }
            public DateTime TransactionDateTime { get; set; }
        }

        public class TicketingTransaction
        {
            public string TicketingTicketID { get; set; }
            public string EventCode { get; set; }
            public string TicketingEventID { get; set; }
            public string MasterCustomerID { get; set; }
            public string TransactionID { get; set; }
            public string PriceCategoryName { get; set; }
            public string PriceClassName { get; set; }
            public string PriceClassCode { get; set; }
            public string SeatAttribute { get; set; }
            public string SeatNoRow { get; set; }
            public string SeatNoSeat { get; set; }
            public string SeatNoSection { get; set; }
            public string SeatType { get; set; }
            public string Level { get; set; }
            public string TicketType { get; set; }
            public string DeliveryMethod { get; set; }
            public string DeliveryAddress1 { get; set; }
            public string DeliveryAddress2 { get; set; }
            public string DeliveryAddress3 { get; set; }
            public string DeliveryAddressCountry { get; set; }
            public decimal BasePriceGrossUnitPrice { get; set; }
            public decimal DiscountAmount { get; set; }
            public decimal PayTicketAmount { get; set; }
            public bool IsFromWSCreate { get; set; }
        }
        public class TicketingTicLvFee
        {
            public string TicketingTicketID { get; set; }
            public string TransactionID { get; set; }
            public string TicketOutsideChargeFeeName { get; set; }
            public decimal TicketOutsideChargeFeeAmount { get; set; }
        }
        public class TicketingTxnHeaderLvFee
        {
            public string TransactionID { get; set; }
            public string TransactionOutsideChargeFeeName { get; set; }
            public decimal TransactionOutsideChargeFeeAmount { get; set; }
        }
        public class TicketingPayment
        {
            public string TransactionID { get; set; }
            public string PaymentMethod { get; set; }
            public decimal PaymentAmount { get; set; }
        }
        public class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string? Remarks { get; set; }
        }
        #endregion
    }
}