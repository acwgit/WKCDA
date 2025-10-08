using DataverseModel;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Authentication;
using System.Threading.Tasks;
using WKCDA_FunctionApp__.Net_8._0_.Model;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class UpdateSubscriptionWS : WS_Base
    {
        public UpdateSubscriptionWS(ILogger<UpdateSubscriptionWS> logger) : base(logger)
        {
        }

        [Function("UpdateSubscriptionWS")]
        public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");

            try
            {
                var svc = GetServiceClient(req);

                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                _logger.LogInformation($"Received request body: {requestBody}");

                var subscriptionRequest = JsonConvert.DeserializeObject<SubscriptionRequest>(requestBody);

                if (subscriptionRequest?.subscriptionList == null || subscriptionRequest.subscriptionList.Count == 0)
                {
                    _logger.LogWarning("Invalid request: subscriptionList is empty or null");
                    return new BadRequestObjectResult("Invalid request: subscriptionList is empty or null");
                }

                var masterCustomerIds = new List<string>();
                var emails = new List<string>();
                var results = new List<SubscriptionResult>();
                var mappings = GetMappings();
                var ecOptInChannel = GetOptInChannel(svc);

                // Process each subscription item
                foreach (var item in subscriptionRequest.subscriptionList)
                {
                    var result = new SubscriptionResult
                    {
                        MasterCustomerID = item.MasterCustomerID,
                        //Email = item.Email,
                        //WK_eNews = item.WK_eNews,
                        //MPlus_eNews = item.MPlus_eNews,
                        Success=false
                    };

                    // Validate MasterCustomerID and Email
                    if (string.IsNullOrEmpty(item.MasterCustomerID) && string.IsNullOrEmpty(item.Email))
                    {
                        result.Remarks = "Please provide Email or MasterCustomerID";
                        _logger.LogWarning($"Invalid subscription item: MasterCustomerID: {item.MasterCustomerID}, Email: {item.Email} - Both are missing");
                    }
                    else
                    {
                        result.Remarks = "Processed successfully";
                        if (!string.IsNullOrEmpty(item.MasterCustomerID))
                        {
                            masterCustomerIds.Add(item.MasterCustomerID);
                        }
                        if (!string.IsNullOrEmpty(item.Email))
                        {
                            emails.Add(item.Email);
                        }
                    }

                    results.Add(result);
                    _logger.LogInformation($"Processing subscription for MasterCustomerID: {item.MasterCustomerID}, Email: {item.Email}, WK_eNews: {item.WK_eNews}, MPlus_eNews: {item.MPlus_eNews}, Remark: {result.Remarks}");
                }

                // Query contacts for valid MasterCustomerIDs or Emails
                var contactCollection = GetContactByMasterCustomerIdOrEmail(svc, masterCustomerIds.ToArray(), emails.ToArray());
                _logger.LogInformation($"Retrieved {contactCollection.Entities.Count} contacts for {masterCustomerIds.Count} MasterCustomerIDs and {emails.Count} Emails");

                foreach (var result in subscriptionRequest.subscriptionList)
                {
                    // Find matching contact by MasterCustomerID or Email
                    var matchingContact = contactCollection.Entities.FirstOrDefault(c =>
                        (!string.IsNullOrEmpty(result.MasterCustomerID) && c.GetAttributeValue<string>("wkcda_mastercustomerid") == result.MasterCustomerID) ||
                        (!string.IsNullOrEmpty(result.Email) && c.GetAttributeValue<string>("emailaddress1") == result.Email));

                    if (matchingContact != null)
                    {
                        try
                        {
                            var subscriptionItem = subscriptionRequest.subscriptionList.FirstOrDefault(i =>
                                (i.MasterCustomerID == result.MasterCustomerID && !string.IsNullOrEmpty(result.MasterCustomerID)) ||
                                (i.Email == result.Email && !string.IsNullOrEmpty(result.Email)));

                            if (subscriptionItem != null)
                            {
                                var contactToUpdate = new Entity("contact", matchingContact.Id);

                                // Map and set fields
                                if (!string.IsNullOrEmpty(subscriptionItem.UnsubscribeAll))
                                    contactToUpdate[mappings["UnsubscribeAll"]] = subscriptionItem.UnsubscribeAll == "Checked" ? true : false;
                                if (!string.IsNullOrEmpty(subscriptionItem.EmailOptinDate1))
                                    contactToUpdate[mappings["EmailOptinDate1"]] = DateTime.Parse(subscriptionItem.EmailOptinDate1);
                                if (!string.IsNullOrEmpty(subscriptionItem.EmailOptinDate2))
                                    contactToUpdate[mappings["EmailOptinDate2"]] = DateTime.Parse(subscriptionItem.EmailOptinDate2);
                                if (!string.IsNullOrEmpty(subscriptionItem.EmailOptOutDate1))
                                    contactToUpdate[mappings["EmailOptOutDate1"]] = DateTime.Parse(subscriptionItem.EmailOptOutDate1);
                                if (!string.IsNullOrEmpty(subscriptionItem.EmailOptOutDate2))
                                    contactToUpdate[mappings["EmailOptOutDate2"]] = DateTime.Parse(subscriptionItem.EmailOptOutDate2);
                                if (!string.IsNullOrEmpty(subscriptionItem.OptInChannel1))
                                    contactToUpdate[mappings["OptInChannel1"]] = GetOptInChannel(ecOptInChannel, subscriptionItem.OptInChannel1);
                                if (!string.IsNullOrEmpty(subscriptionItem.OptInChannel2))
                                    contactToUpdate[mappings["OptInChannel2"]] = GetOptInChannel(ecOptInChannel, subscriptionItem.OptInChannel2);
                                contactToUpdate[mappings["WK_eNews"]] = subscriptionItem.WK_eNews;
                                contactToUpdate[mappings["MPlus_eNews"]] = subscriptionItem.MPlus_eNews;
                                contactToUpdate[mappings["FirstName"]] = subscriptionItem.FirstName;
                                contactToUpdate[mappings["LastName"]] = subscriptionItem.LastName;
                                

                                // Perform the update
                                svc.Update(contactToUpdate);
                                var item = results.FirstOrDefault(o => o.MasterCustomerID == result.MasterCustomerID);
                                if (item != null)
                                {
                                    item.Remarks = $"Contact updated for MasterCustomerID: {result.MasterCustomerID}, Email: {result.Email}";
                                    item.Success = true;
                                    _logger.LogInformation($"Updated contact for MasterCustomerID: {result.MasterCustomerID}, Email: {result.Email}");

                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            var item = results.FirstOrDefault(o => o.MasterCustomerID == result.MasterCustomerID);
                            if (item != null)
                            {
                                item.Remarks = $"Failed to update contact for MasterCustomerID: {result.MasterCustomerID}, Email: {result.Email}. Error: {ex.Message}";
                                item.Success = false;
                                _logger.LogError($"Error updating contact for MasterCustomerID: {result.MasterCustomerID}, Email: {result.Email}. Error: {ex.Message}");

                            }
                        }
                    }
                    else
                    {
                        var item = results.FirstOrDefault(o => o.MasterCustomerID == result.MasterCustomerID);
                        if (item != null)
                        {
                            item.Remarks = $"No contact found for MasterCustomerID: {result.MasterCustomerID}, Email: {result.Email}";
                            item.Success = false;

                            _logger.LogWarning($"No contact found for MasterCustomerID: {result.MasterCustomerID}, Email: {result.Email}");

                        }
                    }
                }
                return new OkObjectResult(results);

                //return new OkObjectResult(new
                //{
                //    success = true,
                //    message = "Subscription update processed successfully",
                //    results = results
                //});
            }
            catch (JsonException ex)
            {
                _logger.LogError($"JSON deserialization error: {ex.Message}");
                return new BadRequestObjectResult("Invalid JSON format in request body");
            }
            catch (AuthenticationException ex)
            {
                return new UnauthorizedObjectResult(ex.Message);
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
        }

        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                { "UnsubscribeAll", "wkcda_unsubscribeall" },
                { "EmailOptinDate1", "wkcda_emailoptindate1" },
                { "EmailOptinDate2", "wkcda_emailoptindate2" },
                { "EmailOptOutDate1", "wkcda_emailoptoutdate1" },
                { "EmailOptOutDate2", "wkcda_emailoptoutdate2" },
                { "OptInChannel1", "wkcda_optinchannel1" },
                { "OptInChannel2", "wkcda_optinchannel2" },
                { "Email", "emailaddress1" },
                { "FirstName", "firstname" },
                { "WK_eNews", "wkcda_westkowloonenewsletter" },
                { "MPlus_eNews", "wkcda_mmembershipenews" },
                { "LastName", "lastname" },
                { "MasterCustomerID", "wkcda_mastercustomerid" }
            };
        }

        private EntityCollection GetContactByMasterCustomerIdOrEmail(ServiceClient svc, string[] masterCustomerIds, string[] emails)
        {
            if ((masterCustomerIds == null || !masterCustomerIds.Any()) && (emails == null || !emails.Any()))
                return new EntityCollection();

            var qe = new QueryExpression("contact");
            qe.ColumnSet.AddColumns("emailaddress1", "wkcda_mastercustomerid");

            var filter = new FilterExpression(LogicalOperator.Or);
            if (masterCustomerIds != null && masterCustomerIds.Any())
            {
                filter.AddCondition("wkcda_mastercustomerid", ConditionOperator.In, masterCustomerIds);
            }
            if (emails != null && emails.Any())
            {
                filter.AddCondition("emailaddress1", ConditionOperator.In, emails);
            }

            qe.Criteria.AddFilter(filter);

            return svc.RetrieveMultiple(qe);
        }

        private EntityCollection GetOptInChannel(ServiceClient svc)
        {
            var qe = new QueryExpression("wkcda_optinchannel");
            qe.ColumnSet.AddColumns("wkcda_channelname");
            return svc.RetrieveMultiple(qe);
        }

        private EntityReference GetOptInChannel(EntityCollection ec, string name)
        {
            return ec.Entities.FirstOrDefault(o => o.GetAttributeValue<string>("wkcda_channelname") == name)?.ToEntityReference();
        }

        public class SubscriptionRequest
        {
            public List<SubscriptionItem> subscriptionList { get; set; }
        }

        public class SubscriptionItem
        {
            public string UnsubscribeAll { get; set; }
            public string EmailOptinDate1 { get; set; }
            public string EmailOptinDate2 { get; set; }
            public string EmailOptOutDate1 { get; set; }
            public string EmailOptOutDate2 { get; set; }
            public string OptInChannel1 { get; set; }
            public string OptInChannel2 { get; set; }
            public string Email { get; set; }
            public string FirstName { get; set; }
            public bool WK_eNews { get; set; }
            public bool MPlus_eNews { get; set; }
            public string LastName { get; set; }
            public string MasterCustomerID { get; set; }
        }

        public class SubscriptionResult
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public string MasterCustomerID { get; set; }
           
          
        }
        public class SubscriptionResult1
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public string MasterCustomerID { get; set; }
            public string Email { get; set; }
            public bool WK_eNews { get; set; }
            public bool MPlus_eNews { get; set; }


        }
    }
}