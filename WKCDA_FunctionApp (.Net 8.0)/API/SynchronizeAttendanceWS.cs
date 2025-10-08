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
using System.Net.Mail;
using System.Text.RegularExpressions;
using System.Linq;

namespace WKCDA_FunctionApp__.Net_8._0_.API
{
    public class SynchronizeAttendanceWSx : WS_Base
    {
        public SynchronizeAttendanceWSx(ILogger<SynchronizeAttendanceWSx> logger) : base(logger) { }

        [Function("SynchronizeAttendanceWS")]
        public async Task<IActionResult> RunAsync(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "data/v9.2/SynchronizeAttendanceWS")] HttpRequest req)
        {
            _logger.LogInformation("APIName: SynchronizeAttendanceWS");

            try
            {
                var svc = GetServiceClient(req);
                var requestBody = await req.ReadFromJsonAsync<RequestBody>();

                if (requestBody?.attens == null || requestBody.attens.Count == 0)
                    return new OkObjectResult(new ResponseBody { Success = false, Remarks = "No records exist." });

                // Process immediately (synchronous) like the original Apex code but without queue
                var response = await ProcessAttendanceRecordsImmediately(svc, requestBody.attens);
                return new OkObjectResult(response);
            }
            catch (JsonException)
            {
                return new OkObjectResult(new ResponseBody { Success = false, Remarks = "Invalid JSON" });
            }
            catch (Exception ex) when (IsUnauthorizedException(ex))
            {
                _logger.LogError(ex, "Authentication failed");
                return new ObjectResult(new { error = "Unauthorized", message = "Authentication failed" })
                {
                    StatusCode = StatusCodes.Status401Unauthorized
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving group accounts");
                return new ObjectResult(new { error = "Bad Request", message = ex.Message })
                {
                    StatusCode = StatusCodes.Status400BadRequest
                };
            }
        }

        protected override Dictionary<string, string> GetMappings()
        {
            return new Dictionary<string, string>
            {
                { "", "" },
            };
        }

        private async Task<ResponseBody> ProcessAttendanceRecordsImmediately(ServiceClient svc, List<attens> attendances)
        {
            var response = new ResponseBody();
            var successCount = 0;
            var errorDetails = new List<string>(); 

            try
            {
                _logger.LogInformation($"Processing {attendances.Count} attendance records");

                foreach (var attendance in attendances)
                {
                    try
                    {
                        _logger.LogInformation($"Processing attendance: CustomerID={attendance.CustomerID}, Email={attendance.PersonEmail}, Event={attendance.Event}");

                        var contactValidationResult = await ValidateAndFindContactAsync(svc, attendance);
                        if (!contactValidationResult.IsValid)
                        {
                            errorDetails.Add(contactValidationResult.ErrorMessage);
                            _logger.LogWarning($"Skipping record: {contactValidationResult.ErrorMessage}");
                            continue;
                        }

                        var existingContact = contactValidationResult.ContactRef;

                        var eventRef = await FindEventByCodeAsync(svc, attendance.Event);
                        if (eventRef == null)
                        {
                            errorDetails.Add($"Event not found: {attendance.Event}");
                            _logger.LogWarning($"Event not found: {attendance.Event}");
                            continue;
                        }

                        var validationResult = ValidateAttendanceRecordData(svc, attendance, existingContact, eventRef);
                        if (!validationResult.IsValid)
                        {
                            errorDetails.Add(validationResult.ErrorMessage);
                            _logger.LogWarning($"Validation failed for attendance record: {validationResult.ErrorMessage}");
                            continue;
                        }

                        var attendanceId = await CreateAttendanceRecordAsync(svc, attendance, existingContact, eventRef);
                        if (attendanceId != Guid.Empty)
                        {
                            successCount++;
                            _logger.LogInformation($"Successfully created attendance record: {attendanceId}");
                        }
                        else
                        {
                            errorDetails.Add($"Failed to create attendance record for Event: {attendance.Event}");
                            _logger.LogWarning($"Failed to create attendance record for Event: {attendance.Event}");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (ex.Message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("authorization failed", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new Exception($"unauthorized: {ex.Message}");
                        }
                        else
                        {
                            errorDetails.Add($"Error processing record: {ex.Message}");
                            _logger.LogError(ex, $"Error processing attendance record");
                        }
                    }
                }

                if (successCount > 0)
                {
                    response.Success = true;
                    response.Remarks = $"Synchronize Attendance starts.";

                    if (errorDetails.Count > 0)
                    {
                        var uniqueErrors = errorDetails.Distinct().Take(3).ToList();
                        response.Remarks += $". Some records failed: {string.Join("; ", uniqueErrors)}";
                    }

                    response.QueueId = Guid.NewGuid().ToString();
                }
                else
                {
                    response.Success = false;
                    var uniqueErrors = errorDetails.Distinct().Take(5).ToList();
                    response.Remarks = $"Synchronize Attendance failed: {string.Join("; ", uniqueErrors)}";
                }

                _logger.LogInformation($"Processing completed: {successCount} successful, {errorDetails.Count} failed");
            }
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

        private bool IsUnauthorizedException(Exception ex)
        {
            if (ex == null) return false;

            var message = ex.Message.ToLowerInvariant();
            return message.Contains("unauthorized") ||
                   message.Contains("authorization failed") ||
                   message.Contains("authentication failed") ||
                   ex.InnerException != null && IsUnauthorizedException(ex.InnerException);
        }

        private async Task<ContactValidationResult> ValidateAndFindContactAsync(ServiceClient svc, attens attendance)
        {
            bool hasEmail = !string.IsNullOrWhiteSpace(attendance.PersonEmail);
            bool hasCustomerId = !string.IsNullOrWhiteSpace(attendance.CustomerID);

            if (!hasEmail && !hasCustomerId)
            {
                return ContactValidationResult.Failure("Either Email or CustomerID is required");
            }

            if (hasEmail && !hasCustomerId)
            {
                if (!IsValidEmail(attendance.PersonEmail))
                {
                    return ContactValidationResult.Failure($"Invalid email format: {attendance.PersonEmail}");
                }

                var contactByEmail = await FindContactByEmailAsync(svc, attendance.PersonEmail);
                if (contactByEmail == null)
                {
                    return ContactValidationResult.Failure($"No contact found with email: {attendance.PersonEmail}");
                }

                return ContactValidationResult.Success(contactByEmail);
            }

            if (!hasEmail && hasCustomerId)
            {
                var contactByCustomerId = await FindContactByCustomerIdAsync(svc, attendance.CustomerID);
                if (contactByCustomerId == null)
                {
                    return ContactValidationResult.Failure($"No contact found with CustomerID: {attendance.CustomerID}");
                }

                return ContactValidationResult.Success(contactByCustomerId);
            }

            if (hasEmail && hasCustomerId)
            {
                if (!IsValidEmail(attendance.PersonEmail))
                {
                    return ContactValidationResult.Failure($"Invalid email format: {attendance.PersonEmail}");
                }

                var contactByCustomerId = await FindContactByCustomerIdAsync(svc, attendance.CustomerID);
                if (contactByCustomerId == null)
                {
                    return ContactValidationResult.Failure($"No contact found with CustomerID: {attendance.CustomerID}");
                }

                var contactDetails = await GetContactDetailsAsync(svc, contactByCustomerId.Id);
                if (contactDetails == null)
                {
                    return ContactValidationResult.Failure($"Unable to retrieve contact details for CustomerID: {attendance.CustomerID}");
                }

                var contactEmail = contactDetails.GetAttributeValue<string>("emailaddress1");
                if (string.IsNullOrWhiteSpace(contactEmail))
                {
                    return ContactValidationResult.Failure($"Contact with CustomerID {attendance.CustomerID} does not have an email address");
                }

                if (!contactEmail.Equals(attendance.PersonEmail, StringComparison.OrdinalIgnoreCase))
                {
                    return ContactValidationResult.Failure($"Email mismatch: Provided email '{attendance.PersonEmail}' does not match the email for CustomerID '{attendance.CustomerID}'");
                }

                return ContactValidationResult.Success(contactByCustomerId);
            }

            return ContactValidationResult.Failure("Invalid combination of Email and CustomerID");
        }

        private async Task<EntityReference> FindContactByEmailAsync(ServiceClient svc, string email)
        {
            return await Task.Run(() =>
            {
                QueryExpression query = new QueryExpression("contact")
                {
                    ColumnSet = new ColumnSet("contactid", "emailaddress1", "wkcda_mastercustomerid"),
                    TopCount = 1
                };

                query.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, email);

                var result = svc.RetrieveMultiple(query);
                return result.Entities.Count > 0 ? result.Entities[0].ToEntityReference() : null;
            });
        }

        private async Task<EntityReference> FindContactByCustomerIdAsync(ServiceClient svc, string customerId)
        {
            return await Task.Run(() =>
            {
                QueryExpression query = new QueryExpression("contact")
                {
                    ColumnSet = new ColumnSet("contactid", "emailaddress1", "wkcda_mastercustomerid"),
                    TopCount = 1
                };

                query.Criteria.AddCondition("wkcda_mastercustomerid", ConditionOperator.Equal, customerId);

                var result = svc.RetrieveMultiple(query);
                return result.Entities.Count > 0 ? result.Entities[0].ToEntityReference() : null;
            });
        }

        private async Task<Entity> GetContactDetailsAsync(ServiceClient svc, Guid contactId)
        {
            return await Task.Run(() =>
            {
                try
                {
                    return svc.Retrieve("contact", contactId, new ColumnSet("emailaddress1", "wkcda_mastercustomerid"));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error retrieving contact details for ID: {contactId}");
                    return null;
                }
            });
        }

        private ValidationResult ValidateRequiredFields(attens attendance)
        {
            if (string.IsNullOrWhiteSpace(attendance.Event))
                return ValidationResult.Failure("Event is required");

            if (string.IsNullOrWhiteSpace(attendance.Status))
                return ValidationResult.Failure("Status is required");

            if (string.IsNullOrWhiteSpace(attendance.CheckInTime))
                return ValidationResult.Failure("CheckInTime is required");

            return ValidationResult.Success();
        }

        private ValidationResult ValidateAttendanceRecordData(ServiceClient svc, attens attendance, EntityReference contactRef, EntityReference eventRef)
        {
            var errors = new List<string>();

            if (contactRef == null || contactRef.Id == Guid.Empty)
                errors.Add("Contact reference is invalid");

            if (eventRef == null || eventRef.Id == Guid.Empty)
                errors.Add("Event reference is invalid");

            var statusOptionSet = GetStatusOptionSetValue(svc, attendance.Status);
            if (statusOptionSet.Value == 100000000)
                errors.Add($"Invalid Status value: {attendance.Status}.");

            if (!DateTime.TryParse(attendance.CheckInTime, out DateTime checkInTime))
                errors.Add($"Invalid CheckInTime format: {attendance.CheckInTime}. Expected format: yyyy-MM-dd HH:mm:ss");
            else
            {
                if (checkInTime > DateTime.UtcNow.AddHours(24))
                    errors.Add($"CheckInTime cannot be more than 24 hours in the future: {checkInTime}");
            }

            if (!string.IsNullOrWhiteSpace(attendance.NoOfRegistrants))
            {
                if (!double.TryParse(attendance.NoOfRegistrants, out double noOfRegistrants))
                    errors.Add($"Invalid NoOfRegistrants format: {attendance.NoOfRegistrants}. Must be a number");
                else if (noOfRegistrants < 0)
                    errors.Add($"NoOfRegistrants cannot be negative: {noOfRegistrants}");
                else if (noOfRegistrants > 1000)
                    errors.Add($"NoOfRegistrants is too large: {noOfRegistrants}. Maximum allowed: 1000");
            }

            if (!string.IsNullOrWhiteSpace(attendance.Remarks) && attendance.Remarks.Length > 2000)
                errors.Add($"Remarks exceeds maximum length of 2000 characters: {attendance.Remarks.Length}");

            if (!string.IsNullOrWhiteSpace(attendance.RegistrantFirstName) && attendance.RegistrantFirstName.Length > 100)
                errors.Add($"RegistrantFirstName exceeds maximum length of 100 characters: {attendance.RegistrantFirstName.Length}");

            if (!string.IsNullOrWhiteSpace(attendance.RegistrantLastName) && attendance.RegistrantLastName.Length > 100)
                errors.Add($"RegistrantLastName exceeds maximum length of 100 characters: {attendance.RegistrantLastName.Length}");

            if (!string.IsNullOrWhiteSpace(attendance.RegistrantSalutation) && attendance.RegistrantSalutation.Length > 50)
                errors.Add($"RegistrantSalutation exceeds maximum length of 50 characters: {attendance.RegistrantSalutation.Length}");

            if (!string.IsNullOrWhiteSpace(attendance.Mobile) && attendance.Mobile.Length > 50)
                errors.Add($"Mobile exceeds maximum length of 50 characters: {attendance.Mobile.Length}");

            if (!string.IsNullOrWhiteSpace(attendance.PersonEmail) && attendance.PersonEmail.Length > 100)
                errors.Add($"PersonEmail exceeds maximum length of 100 characters: {attendance.PersonEmail.Length}");

            if (!string.IsNullOrWhiteSpace(attendance.PersonEmail) && !IsValidEmail(attendance.PersonEmail))
                errors.Add($"Invalid email format: {attendance.PersonEmail}");

            if (!string.IsNullOrWhiteSpace(attendance.Mobile) && !IsValidMobile(attendance.Mobile))
                errors.Add($"Invalid mobile number format: {attendance.Mobile}");

            if (errors.Count > 0)
                return ValidationResult.Failure(string.Join("; ", errors));

            return ValidationResult.Success();
        }

        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        private bool IsValidMobile(string mobile)
        {
            return Regex.IsMatch(mobile, @"^\+?[\d\s\-\(\)]{10,}$");
        }

        private async Task<EntityReference> FindEventByCodeAsync(ServiceClient svc, string eventCode)
        {
            return await Task.Run(() =>
            {
                QueryExpression query = new QueryExpression("msevtmgt_event")
                {
                    ColumnSet = new ColumnSet("msevtmgt_eventid"),
                    TopCount = 1
                };

                var filter = new FilterExpression(LogicalOperator.Or);
                filter.AddCondition("wkcda_eventcode", ConditionOperator.Equal, eventCode);
                filter.AddCondition("msevtmgt_name", ConditionOperator.Equal, eventCode);

                query.Criteria = filter;

                var result = svc.RetrieveMultiple(query);

                if (result.Entities.Count > 0)
                {
                    _logger.LogInformation($"Found event in msevtmgt_event: ID={result.Entities[0].Id}, Code={eventCode}");
                    return result.Entities[0].ToEntityReference();
                }

                QueryExpression fallbackQuery = new QueryExpression("wkcda_event")
                {
                    ColumnSet = new ColumnSet("wkcda_eventid"),
                    TopCount = 1
                };

                var fallbackFilter = new FilterExpression(LogicalOperator.Or);
                fallbackFilter.AddCondition("wkcda_eventcode", ConditionOperator.Equal, eventCode);
                fallbackFilter.AddCondition("wkcda_name", ConditionOperator.Equal, eventCode);

                fallbackQuery.Criteria = fallbackFilter;

                var fallbackResult = svc.RetrieveMultiple(fallbackQuery);

                if (fallbackResult.Entities.Count > 0)
                {
                    _logger.LogInformation($"Found event in wkcda_event: ID={fallbackResult.Entities[0].Id}, Code={eventCode}");
                    return fallbackResult.Entities[0].ToEntityReference();
                }

                _logger.LogWarning($"Event not found in any table: {eventCode}");
                return null;
            });
        }

        private async Task<Guid> CreateAttendanceRecordAsync(ServiceClient svc, attens attendance, EntityReference contactRef, EntityReference eventRef)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var existingQuery = new QueryExpression("wkcda_attendance")
                    {
                        ColumnSet = new ColumnSet("wkcda_attendanceid"),
                        TopCount = 1
                    };

                    existingQuery.Criteria.AddCondition("wkcda_member", ConditionOperator.Equal, contactRef.Id);
                    existingQuery.Criteria.AddCondition("wkcda_event", ConditionOperator.Equal, eventRef.Id);

                    var existingRecords = svc.RetrieveMultiple(existingQuery);

                    Entity attendanceRecord;

                    if (existingRecords.Entities.Count > 0)
                    {
                        attendanceRecord = new Entity("wkcda_attendance", existingRecords.Entities[0].Id);
                        _logger.LogInformation($"Updating existing attendance record: {existingRecords.Entities[0].Id}");
                    }
                    else
                    {
                        attendanceRecord = new Entity("wkcda_attendance");
                        _logger.LogInformation("Creating new attendance record");
                    }

                    attendanceRecord["wkcda_member"] = contactRef;
                    attendanceRecord["wkcda_event"] = eventRef;
                    attendanceRecord["wkcda_attendancestatus"] = GetStatusOptionSetValue(svc, attendance.Status);

                    if (DateTime.TryParse(attendance.CheckInTime, out DateTime checkInTime))
                    {
                        attendanceRecord["wkcda_checkintime"] = checkInTime;
                    }
                    else
                    {
                        _logger.LogWarning($"Invalid CheckInTime format: {attendance.CheckInTime}");
                        return Guid.Empty;
                    }

                    if (!string.IsNullOrWhiteSpace(attendance.NoOfRegistrants) &&
                        double.TryParse(attendance.NoOfRegistrants, out double noOfRegistrants))
                    {
                        attendanceRecord["wkcda_noofregistrants"] = noOfRegistrants;
                    }

                    if (!string.IsNullOrWhiteSpace(attendance.Remarks))
                        attendanceRecord["wkcda_remarks"] = StringHelper.Truncate(attendance.Remarks, 2000);

                    if (!string.IsNullOrWhiteSpace(attendance.RegistrantFirstName))
                        attendanceRecord["wkcda_registrantfirstname"] = StringHelper.Truncate(attendance.RegistrantFirstName, 100);

                    if (!string.IsNullOrWhiteSpace(attendance.RegistrantLastName))
                        attendanceRecord["wkcda_registrantlastname"] = StringHelper.Truncate(attendance.RegistrantLastName, 100);

                    if (!string.IsNullOrWhiteSpace(attendance.RegistrantSalutation))
                        attendanceRecord["wkcda_registrantsalutation"] = StringHelper.Truncate(attendance.RegistrantSalutation, 50);

                    if (!string.IsNullOrWhiteSpace(attendance.Mobile))
                        attendanceRecord["wkcda_mobile"] = StringHelper.Truncate(attendance.Mobile, 50);

                    if (!string.IsNullOrWhiteSpace(attendance.PersonEmail))
                        attendanceRecord["wkcda_participantemail"] = StringHelper.Truncate(attendance.PersonEmail, 100);

                    Guid attendanceId;

                    if (existingRecords.Entities.Count > 0)
                    {
                        svc.Update(attendanceRecord);
                        attendanceId = existingRecords.Entities[0].Id;
                        _logger.LogInformation($"Successfully updated attendance record: {attendanceId}");
                    }
                    else
                    {
                        attendanceId = svc.Create(attendanceRecord);
                        _logger.LogInformation($"Successfully created attendance record: {attendanceId}");
                    }

                    return attendanceId;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating/updating attendance record");
                    return Guid.Empty;
                }
            });
        }

        private OptionSetValue GetStatusOptionSetValue(ServiceClient svc, string status)
        {
            int? optionSetValue = CRMEntityHelper.getOptionSetValue(
                svc,
                "wkcda_attendance", 
                "wkcda_attendancestatus", 
                status,
                _logger
            );

            return optionSetValue.HasValue
                ? new OptionSetValue(optionSetValue.Value)
                : new OptionSetValue(100000000); 
        }

        #region Helper Classes

        public class ValidationResult
        {
            public bool IsValid { get; set; }
            public string ErrorMessage { get; set; }

            public static ValidationResult Success() => new ValidationResult { IsValid = true };
            public static ValidationResult Failure(string message) => new ValidationResult { IsValid = false, ErrorMessage = message };
        }

        public class ContactValidationResult
        {
            public bool IsValid { get; set; }
            public string ErrorMessage { get; set; }
            public EntityReference ContactRef { get; set; }

            public static ContactValidationResult Success(EntityReference contactRef) =>
                new ContactValidationResult { IsValid = true, ContactRef = contactRef };

            public static ContactValidationResult Failure(string message) =>
                new ContactValidationResult { IsValid = false, ErrorMessage = message };
        }

        #endregion

        #region Request/Response Classes

        public class RequestBody
        {
            public List<attens> attens { get; set; }
        }

        public class attens
        {
            public string PersonEmail { get; set; }
            public string CustomerID { get; set; }
            public string Event { get; set; }
            public string Status { get; set; }
            public string CheckInTime { get; set; }
            public string NoOfRegistrants { get; set; }
            public string EventTransaction { get; set; }
            public string Remarks { get; set; }
            public string RegistrantFirstName { get; set; }
            public string RegistrantLastName { get; set; }
            public string RegistrantSalutation { get; set; }
            public string Mobile { get; set; }
        }

        public class ResponseBody
        {
            public bool Success { get; set; }
            public string Remarks { get; set; }
            public string QueueId { get; set; }
        }

        #endregion
    }
    public static class StringHelper
    {
        public static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }
}