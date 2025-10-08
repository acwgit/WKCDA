using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataverseModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using WKCDA_FunctionApp__.Net_8._0_.Helper;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class OnlineRegistrationWS : WS_Base
    {
        public OnlineRegistrationWS(ILogger<OnlineRegistrationWS> logger) : base(logger) { }

        [Function("OnlineRegistrationWS")]
        public async Task<IActionResult> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/OnlineRegistrationWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: OnlineRegistrationWS");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.newTransactionList == null || !requestBody.newTransactionList.Any())
                    return new OkObjectResult(new ResponseBody_Item { Success = false, Remarks = "Invalid request body" });

                var response = new List<ResponseBody_Item>();

                foreach (var txn in requestBody.newTransactionList)
                {
                    var resultItem = new ResponseBody_Item
                    {
                        requestIndex = requestBody.newTransactionList.IndexOf(txn),
                        EventTransaction = txn,
                        Success = false,
                        Remarks = null
                    };
                    response.Add(resultItem);

                    #region Validations
                    if (string.IsNullOrEmpty(txn.EventCode))
                    {
                        resultItem.Remarks = "Please provide Event Code.";
                        continue;
                    }

                    if (string.IsNullOrEmpty(txn.CustomerID) && string.IsNullOrEmpty(txn.Email))
                    {
                        resultItem.Remarks = "Please provide CustomerID or Email.";
                        continue;
                    }

                    if (txn.EventTransaction == null || string.IsNullOrEmpty(txn.EventTransaction.RegistrationDate))
                    {
                        resultItem.Remarks = "Please provide RegistrationDate.";
                        continue;
                    }
                    #endregion

                    try
                    {
                        #region Create EventTransaction
                        var newEt = new Entity("wkcda_eventtransaction");

                        newEt["wkcda_status"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_eventtransaction", "wkcda_status", txn.EventTransaction.Status, _logger) ?? 0);
                        newEt["wkcda_seatnosection"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_eventtransaction", "wkcda_seatnosection", txn.EventTransaction.SeatSection, _logger) ?? 0);
                        newEt["wkcda_seatnorow"] = txn.EventTransaction.SeatRow;
                        newEt["wkcda_seatnoseat"] = txn.EventTransaction.SeatNo;
                        newEt["wkcda_salutation"] = txn.EventTransaction.Salutation;
                        for (int i = 1; i <= 20; i++)
                        {
                            var prop = typeof(EventTransactionInput).GetProperty($"Remark{i}");
                            if (prop != null)
                            {
                                newEt[$"wkcda_answerremark{i}"] = prop.GetValue(txn.EventTransaction);
                            }
                        }
                        newEt["wkcda_registrationdate"] = DateOnly.Parse(txn.EventTransaction.RegistrationDate).ToDateTime(TimeOnly.MinValue);
                        newEt["wkcda_registrantfirstnameeng"] = txn.EventTransaction.RegistrantFirstName;
                        newEt["wkcda_registrantlastnameeng"] = txn.EventTransaction.RegistrantLastName;
                        newEt["wkcda_mobilephoneno"] = txn.EventTransaction.Mobile;
                        newEt["wkcda_programmevenue"] = txn.EventTransaction.ProgrammeVenue;
                        newEt["wkcda_numberofparticipants"] = Convert.ToDouble(txn.EventTransaction.NoofParticipants);
                        newEt["wkcda_numberoftickets"] = txn.EventTransaction.NumofTickets;
                        newEt["wkcda_paymentmethod"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_eventtransaction", "wkcda_paymentmethod", txn.EventTransaction.PaymentMethod, _logger) ?? 0);
                        newEt["wkcda_discountcode"] = txn.EventTransaction.DiscountCode;
                        newEt["wkcda_price"] = new Money(txn.EventTransaction.Price);
                        newEt["wkcda_pricezoneclasstier"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_eventtransaction", "wkcda_pricezoneclasstier", txn.EventTransaction.PriceZone, _logger) ?? 0);
                        newEt["wkcda_purchasingmethod"] = CRMEntityHelper.getMutiselectOptionValues(svc, "wkcda_eventtransaction", "wkcda_purchasingmethod", txn.EventTransaction.PurchasingMethod);
                        newEt["wkcda_registrationsource"] = new OptionSetValue(CRMEntityHelper.getOptionSetValue(svc, "wkcda_eventtransaction", "wkcda_registrationsource", txn.EventTransaction.RegistrationSource, _logger) ?? 0);
                        newEt["wkcda_eventrelated"] = GetEventByCode(svc, txn.EventCode);
                        newEt["wkcda_personalemail"] = txn.Email;
                        var eventRef = GetEventByCode(svc, txn.EventCode);
                        if (eventRef != null)
                        newEt["wkcda_eventrelated"] = eventRef;
                        var contactRef = GetContactReference(svc, txn.CustomerID, txn.Email);
                        if (contactRef != null)
                        {
                            newEt["wkcda_membername"] = contactRef;
                        }
                        var createdId = svc.Create(newEt);
                        resultItem.Success = createdId != Guid.Empty;

                        #endregion
                    }
                  
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("unauthorized"))
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
                    //catch (Exception ex)
                    //{
                    //    resultItem.Success = false;
                    //    resultItem.Remarks = ex.Message;
                    //    _logger.LogError(ex, "Error creating Event Transaction");
                    //}
                }

                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new ResponseBody_Item { Success = false, Remarks = "Invalid JSON" });
            }
            catch (AuthenticationException ex)
            {
                return new UnauthorizedObjectResult(ex.Message);
            }
            catch (Exception ex)
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

        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                { "RegistrationDate", "wkcda_registrationdate" },
                { "Event", "wkcda_eventname" },
                { "PersonAccount", "wkcda_membername" }, // lookup
                { "Status", "wkcda_status" },
                { "Salutation", "wkcda_salutation" },
                { "RegistrantFirstName", "wkcda_registrantfirstnameeng" },
                { "RegistrantLastName", "wkcda_registrantlastnameeng" },
                { "Mobile", "wkcda_mobilephoneno" },
                { "ProgrammeVenue", "wkcda_programmevenue" },
                { "NoofParticipants", "wkcda_numberofparticipants" },
                { "NumofTickets", "wkcda_numberoftickets" },
                { "PaymentMethod", "wkcda_paymentmethod" },
                { "DiscountCode", "wkcda_discountcode" },
                { "Price", "wkcda_price" },
                { "PriceZone", "wkcda_pricezoneclasstier" },
                { "SeatRow", "wkcda_seatnorow" },
                { "SeatNo", "wkcda_seatnoseat" },
                { "SeatSection", "wkcda_seatnosection" },
                { "PurchasingMethod", "wkcda_purchasingmethod" },
                { "RegistrationSource", "wkcda_registrationsource" },
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

        private EntityReference? GetEventByCode(ServiceClient svc, string eventCode)
        {
            if (string.IsNullOrEmpty(eventCode)) return null;
            var qe = new QueryExpression("msevtmgt_event") { ColumnSet = new ColumnSet("msevtmgt_eventid") };
            qe.Criteria.AddCondition("wkcda_eventcode", ConditionOperator.Equal, eventCode);
            var e = svc.RetrieveMultiple(qe).Entities.FirstOrDefault();
            return e != null ? new EntityReference("msevtmgt_event", e.Id) : null;
        }

        #region Request/Response Classes
        public class RequestBody
        {
            public required List<NewTransaction> newTransactionList { get; set; }
        }

        public class NewTransaction
        {
            public string EventCode { get; set; }
            public string Email { get; set; }
            public string CustomerID { get; set; }
            public EventTransactionInput EventTransaction { get; set; }
        }

        public class EventTransactionInput
        {
            public string RegistrationDate { get; set; }
            public string Event { get; set; }
            public string PersonAccount { get; set; }
            public string Status { get; set; }
            public string Salutation { get; set; }
            public string RegistrantFirstName { get; set; }
            public string RegistrantLastName { get; set; }
            public string Mobile { get; set; }
            public string ProgrammeVenue { get; set; }
            public decimal NoofParticipants { get; set; }
            public double NumofTickets { get; set; }
            public string PaymentMethod { get; set; }
            public string DiscountCode { get; set; }
            public decimal Price { get; set; }
            public string PriceZone { get; set; }
            public string SeatRow { get; set; }
            public string SeatNo { get; set; }
            public string SeatSection { get; set; }
            public string PurchasingMethod { get; set; }
            public string RegistrationSource { get; set; }
            public string SessionCode { get; set; }
            public string Remark1 { get; set; }
            public string Remark2 { get; set; }
            public string Remark3 { get; set; }
            public string Remark4 { get; set; }
            public string Remark5 { get; set; }
            public string Remark6 { get; set; }
            public string Remark7 { get; set; }
            public string Remark8 { get; set; }
            public string Remark9 { get; set; }
            public string Remark10 { get; set; }
            public string Remark11 { get; set; }
            public string Remark12 { get; set; }
            public string Remark13 { get; set; }
            public string Remark14 { get; set; }
            public string Remark15 { get; set; }
            public string Remark16 { get; set; }
            public string Remark17 { get; set; }
            public string Remark18 { get; set; }
            public string Remark19 { get; set; }
            public string Remark20 { get; set; }
        }
        public class ResponseBody_Item
        {
            public bool Success { get; set; }
            public string? Remarks { get; set; }
            public NewTransaction EventTransaction { get; set; }

            [JsonIgnore]
            public int requestIndex;
        }
        #endregion
    }
}