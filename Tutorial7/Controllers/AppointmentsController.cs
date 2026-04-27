using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Data;
using Tutorial7.DTOs;

namespace Tutorial7.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AppointmentsController : ControllerBase
{
    private readonly string _connectionString;

    public AppointmentsController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
                            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing.");
    }

    [HttpGet]
    public async Task<IActionResult> GetAppointments([FromQuery] string? status, [FromQuery] string? patientLastName)
    {
        const string query = @"
SELECT
    a.IdAppointment,
    a.AppointmentDate,
    a.Status,
    a.Reason,
    p.FirstName + N' ' + p.LastName AS PatientFullName,
    p.Email AS PatientEmail
FROM dbo.Appointments a
JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
WHERE (@Status IS NULL OR a.Status = @Status)
  AND (@PatientLastName IS NULL OR p.LastName = @PatientLastName)
ORDER BY a.AppointmentDate;";

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(query, connection);

        var statusParam = command.Parameters.Add("@Status", SqlDbType.NVarChar, 30);
        statusParam.Value = string.IsNullOrWhiteSpace(status) ? DBNull.Value : status.Trim();

        var patientLastNameParam = command.Parameters.Add("@PatientLastName", SqlDbType.NVarChar, 80);
        patientLastNameParam.Value = string.IsNullOrWhiteSpace(patientLastName) ? DBNull.Value : patientLastName.Trim();

        await using var reader = await command.ExecuteReaderAsync();
        var appointments = new List<AppointmentListDto>();

        while (await reader.ReadAsync())
        {
            appointments.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32(0),
                AppointmentDate = reader.GetDateTime(1),
                Status = reader.GetString(2),
                Reason = reader.GetString(3),
                PatientFullName = reader.GetString(4),
                PatientEmail = reader.GetString(5)
            });
        }

        return Ok(appointments);
    }

    [HttpGet("{idAppointment:int}")]
    public async Task<IActionResult> GetAppointmentById([FromRoute] int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var appointment = await GetAppointmentDetailsInternalAsync(connection, idAppointment);
        if (appointment is null)
        {
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });
        }

        return Ok(appointment);
    }

    [HttpPost]
    public async Task<IActionResult> CreateAppointment([FromBody] CreateAppointmentRequestDto request)
    {
        var validationError = ValidateCreateRequest(request);
        if (validationError is not null)
        {
            return BadRequest(new ErrorResponseDto { Message = validationError });
        }

        var appointmentDate = TrimToSeconds(request.AppointmentDate);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        if (!await IsPatientActiveAsync(connection, request.IdPatient))
        {
            return NotFound(new ErrorResponseDto { Message = "Patient does not exist or is inactive." });
        }

        if (!await IsDoctorActiveAsync(connection, request.IdDoctor))
        {
            return NotFound(new ErrorResponseDto { Message = "Doctor does not exist or is inactive." });
        }

        if (await HasScheduledConflictAsync(connection, request.IdDoctor, appointmentDate, null))
        {
            return Conflict(new ErrorResponseDto
            {
                Message = "Doctor already has a scheduled appointment at this time."
            });
        }

        const string insertQuery = @"
INSERT INTO dbo.Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason, InternalNotes)
VALUES (@IdPatient, @IdDoctor, @AppointmentDate, @Status, @Reason, NULL);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

        await using var insertCommand = new SqlCommand(insertQuery, connection);
        insertCommand.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        insertCommand.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        insertCommand.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = appointmentDate;
        insertCommand.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = "Scheduled";
        insertCommand.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason.Trim();

        var createdIdObj = await insertCommand.ExecuteScalarAsync();
        var idAppointment = Convert.ToInt32(createdIdObj);

        var createdAppointment = await GetAppointmentDetailsInternalAsync(connection, idAppointment);
        object responseBody = createdAppointment is null
            ? new { IdAppointment = idAppointment }
            : createdAppointment;

        return CreatedAtAction(
            nameof(GetAppointmentById),
            new { idAppointment },
            responseBody);
    }

    [HttpPut("{idAppointment:int}")]
    public async Task<IActionResult> UpdateAppointment([FromRoute] int idAppointment,
        [FromBody] UpdateAppointmentRequestDto request)
    {
        var validationError = ValidateUpdateRequest(request);
        if (validationError is not null)
        {
            return BadRequest(new ErrorResponseDto { Message = validationError });
        }

        var appointmentDate = TrimToSeconds(request.AppointmentDate);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var currentState = await GetAppointmentStateAsync(connection, idAppointment);
        if (currentState is null)
        {
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });
        }

        if (currentState.Value.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase) &&
            TrimToSeconds(currentState.Value.AppointmentDate) != appointmentDate)
        {
            return BadRequest(new ErrorResponseDto
            {
                Message = "Cannot change appointment date for a completed appointment."
            });
        }

        if (!await IsPatientActiveAsync(connection, request.IdPatient))
        {
            return NotFound(new ErrorResponseDto { Message = "Patient does not exist or is inactive." });
        }

        if (!await IsDoctorActiveAsync(connection, request.IdDoctor))
        {
            return NotFound(new ErrorResponseDto { Message = "Doctor does not exist or is inactive." });
        }

        var normalizedStatus = NormalizeStatus(request.Status);
        var doctorOrDateChanged = currentState.Value.IdDoctor != request.IdDoctor ||
                                  TrimToSeconds(currentState.Value.AppointmentDate) != appointmentDate;
        if (doctorOrDateChanged)
        {
            var hasConflict = await HasScheduledConflictAsync(connection, request.IdDoctor, appointmentDate,
                idAppointment);
            if (hasConflict)
            {
                return Conflict(new ErrorResponseDto
                {
                    Message = "Doctor already has a scheduled appointment at this time."
                });
            }
        }

        const string updateQuery = @"
UPDATE dbo.Appointments
SET IdPatient = @IdPatient,
    IdDoctor = @IdDoctor,
    AppointmentDate = @AppointmentDate,
    Status = @Status,
    Reason = @Reason,
    InternalNotes = @InternalNotes
WHERE IdAppointment = @IdAppointment;";

        await using var updateCommand = new SqlCommand(updateQuery, connection);
        updateCommand.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        updateCommand.Parameters.Add("@IdPatient", SqlDbType.Int).Value = request.IdPatient;
        updateCommand.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = request.IdDoctor;
        updateCommand.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = appointmentDate;
        updateCommand.Parameters.Add("@Status", SqlDbType.NVarChar, 30).Value = normalizedStatus;
        updateCommand.Parameters.Add("@Reason", SqlDbType.NVarChar, 250).Value = request.Reason.Trim();

        var internalNotesParam = updateCommand.Parameters.Add("@InternalNotes", SqlDbType.NVarChar, 500);
        internalNotesParam.Value = string.IsNullOrWhiteSpace(request.InternalNotes)
            ? DBNull.Value
            : request.InternalNotes.Trim();

        var affectedRows = await updateCommand.ExecuteNonQueryAsync();
        if (affectedRows == 0)
        {
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });
        }

        var updatedAppointment = await GetAppointmentDetailsInternalAsync(connection, idAppointment);
        if (updatedAppointment is null)
        {
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });
        }

        return Ok(updatedAppointment);
    }

    [HttpDelete("{idAppointment:int}")]
    public async Task<IActionResult> DeleteAppointment([FromRoute] int idAppointment)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        var currentState = await GetAppointmentStateAsync(connection, idAppointment);
        if (currentState is null)
        {
            return NotFound(new ErrorResponseDto { Message = "Appointment not found." });
        }

        if (currentState.Value.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            return Conflict(new ErrorResponseDto
            {
                Message = "Completed appointment cannot be deleted."
            });
        }

        const string deleteQuery = @"
DELETE FROM dbo.Appointments
WHERE IdAppointment = @IdAppointment;";

        await using var deleteCommand = new SqlCommand(deleteQuery, connection);
        deleteCommand.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;
        await deleteCommand.ExecuteNonQueryAsync();

        return NoContent();
    }

    private static string? ValidateCreateRequest(CreateAppointmentRequestDto request)
    {
        if (request.IdPatient <= 0)
        {
            return "IdPatient must be greater than 0.";
        }

        if (request.IdDoctor <= 0)
        {
            return "IdDoctor must be greater than 0.";
        }

        if (request.AppointmentDate < DateTime.Now)
        {
            return "AppointmentDate cannot be in the past.";
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return "Reason cannot be empty.";
        }

        if (request.Reason.Trim().Length > 250)
        {
            return "Reason cannot be longer than 250 characters.";
        }

        return null;
    }

    private static string? ValidateUpdateRequest(UpdateAppointmentRequestDto request)
    {
        if (request.IdPatient <= 0)
        {
            return "IdPatient must be greater than 0.";
        }

        if (request.IdDoctor <= 0)
        {
            return "IdDoctor must be greater than 0.";
        }

        if (string.IsNullOrWhiteSpace(request.Status))
        {
            return "Status cannot be empty.";
        }

        if (!IsAllowedStatus(request.Status))
        {
            return "Status must be one of: Scheduled, Completed, Cancelled.";
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return "Reason cannot be empty.";
        }

        if (request.Reason.Trim().Length > 250)
        {
            return "Reason cannot be longer than 250 characters.";
        }

        if (!string.IsNullOrWhiteSpace(request.InternalNotes) && request.InternalNotes.Trim().Length > 500)
        {
            return "InternalNotes cannot be longer than 500 characters.";
        }

        return null;
    }

    private static bool IsAllowedStatus(string status)
    {
        return status.Trim().Equals("Scheduled", StringComparison.OrdinalIgnoreCase) ||
               status.Trim().Equals("Completed", StringComparison.OrdinalIgnoreCase) ||
               status.Trim().Equals("Cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeStatus(string status)
    {
        if (status.Trim().Equals("Scheduled", StringComparison.OrdinalIgnoreCase))
        {
            return "Scheduled";
        }

        if (status.Trim().Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            return "Completed";
        }

        return "Cancelled";
    }

    private static async Task<bool> IsPatientActiveAsync(SqlConnection connection, int idPatient)
    {
        const string query = @"
SELECT COUNT(1)
FROM dbo.Patients
WHERE IdPatient = @IdPatient
  AND IsActive = 1;";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@IdPatient", SqlDbType.Int).Value = idPatient;

        var count = (int)(await command.ExecuteScalarAsync() ?? 0);
        return count > 0;
    }

    private static async Task<bool> IsDoctorActiveAsync(SqlConnection connection, int idDoctor)
    {
        const string query = @"
SELECT COUNT(1)
FROM dbo.Doctors
WHERE IdDoctor = @IdDoctor
  AND IsActive = 1;";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = idDoctor;

        var count = (int)(await command.ExecuteScalarAsync() ?? 0);
        return count > 0;
    }

    private static async Task<bool> HasScheduledConflictAsync(SqlConnection connection, int idDoctor,
        DateTime appointmentDate, int? excludedAppointmentId)
    {
        const string query = @"
SELECT COUNT(1)
FROM dbo.Appointments
WHERE IdDoctor = @IdDoctor
  AND AppointmentDate = @AppointmentDate
  AND Status = N'Scheduled'
  AND (@ExcludedAppointmentId IS NULL OR IdAppointment <> @ExcludedAppointmentId);";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@IdDoctor", SqlDbType.Int).Value = idDoctor;
        command.Parameters.Add("@AppointmentDate", SqlDbType.DateTime2).Value = appointmentDate;

        var excludedIdParam = command.Parameters.Add("@ExcludedAppointmentId", SqlDbType.Int);
        excludedIdParam.Value = excludedAppointmentId.HasValue
            ? excludedAppointmentId.Value
            : DBNull.Value;

        var count = (int)(await command.ExecuteScalarAsync() ?? 0);
        return count > 0;
    }

    private static async Task<(string Status, DateTime AppointmentDate, int IdDoctor)?> GetAppointmentStateAsync(
        SqlConnection connection, int idAppointment)
    {
        const string query = @"
SELECT Status, AppointmentDate, IdDoctor
FROM dbo.Appointments
WHERE IdAppointment = @IdAppointment;";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return (reader.GetString(0), reader.GetDateTime(1), reader.GetInt32(2));
    }

    private static DateTime TrimToSeconds(DateTime value)
    {
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.Kind);
    }

    private static async Task<AppointmentDetailsDto?> GetAppointmentDetailsInternalAsync(SqlConnection connection,
        int idAppointment)
    {
        const string query = @"
SELECT
    a.IdAppointment,
    a.AppointmentDate,
    a.Status,
    a.Reason,
    a.InternalNotes,
    a.CreatedAt,
    p.FirstName + N' ' + p.LastName AS PatientFullName,
    p.Email AS PatientEmail,
    p.PhoneNumber AS PatientPhoneNumber,
    d.FirstName + N' ' + d.LastName AS DoctorFullName,
    d.LicenseNumber AS DoctorLicenseNumber,
    s.Name AS SpecializationName
FROM dbo.Appointments a
JOIN dbo.Patients p ON p.IdPatient = a.IdPatient
JOIN dbo.Doctors d ON d.IdDoctor = a.IdDoctor
JOIN dbo.Specializations s ON s.IdSpecialization = d.IdSpecialization
WHERE a.IdAppointment = @IdAppointment;";

        await using var command = new SqlCommand(query, connection);
        command.Parameters.Add("@IdAppointment", SqlDbType.Int).Value = idAppointment;

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new AppointmentDetailsDto
        {
            IdAppointment = reader.GetInt32(0),
            AppointmentDate = reader.GetDateTime(1),
            Status = reader.GetString(2),
            Reason = reader.GetString(3),
            InternalNotes = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetDateTime(5),
            PatientFullName = reader.GetString(6),
            PatientEmail = reader.GetString(7),
            PatientPhoneNumber = reader.GetString(8),
            DoctorFullName = reader.GetString(9),
            DoctorLicenseNumber = reader.GetString(10),
            SpecializationName = reader.GetString(11)
        };
    }
}
