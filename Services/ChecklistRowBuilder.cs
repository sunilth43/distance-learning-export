using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace distanceExport.Services;

/// <summary>
/// For every user in a segment: loads their profile, then for each application that has
/// reached a decision, fetches the decision's checklist and yields one CSV row per item.
/// </summary>
public sealed class ChecklistRowBuilder
{
    private readonly Element451ApiClient _api;
    private readonly ILogger<ChecklistRowBuilder> _logger;
    private readonly Dictionary<string, string?> _majorCodeCache = new();

    private static readonly ExportColumn[] RegisteredAtColumns = { TestRecordFilter.RegisteredAtColumn };
    private static readonly DateTime SubmittedOnCutoff = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    public ChecklistRowBuilder(Element451ApiClient api, ILogger<ChecklistRowBuilder> logger)
    {
        _api = api;
        _logger = logger;
    }

    public async IAsyncEnumerable<Dictionary<string, string?>> BuildRowsAsync(
        string applicationGuid,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var userCount = 0;
        var rowCount = 0;

        var termGuids = await _api.GetActiveTermGuidsAsync(ct);
        if (termGuids.Count == 0)
        {
            _logger.LogWarning("No active terms found; DistanceLearning export will yield no users.");
            yield break;
        }

        var filter = BuildFilter(applicationGuid, termGuids);

        await foreach (var user in _api.GetUsersByAnalyticsFilterAsync(filter, ct))
        {
            ct.ThrowIfCancellationRequested();
            userCount++;

            if (!user.TryGetValue("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            {
                _logger.LogWarning("Segment record #{Index} has no string 'id'; skipping.", userCount);
                continue;
            }
            var userId = idEl.GetString()!;

            var firstName = GetOrNull(user, "first_name");
            var middleName = GetOrNull(user, "middle_name");
            var lastName = GetOrNull(user, "last_name");

            if (TestRecordFilter.IsTestIdentity(firstName, lastName, GetOrNull(user, "email")))
            {
                _logger.LogInformation("User {UserId}: skipped, name or email looks like a test record.", userId);
                continue;
            }

            var schoolIdTask = _api.GetSchoolIdAsync(userId, ct);
            var profileTask = _api.GetUserProfileAsync(userId, ct);

            string? schoolId;
            try
            {
                schoolId = await schoolIdTask;
            }
            catch (SegmentApiException ex)
            {
                _logger.LogError("User {UserId}: failed to load external identifier ({Message})", userId, ex.Message);
                schoolId = null;
            }

            JsonElement? profile;
            try
            {
                profile = await profileTask;
            }
            catch (SegmentApiException ex)
            {
                _logger.LogError("Skipping user {UserId}: failed to load profile ({Message})", userId, ex.Message);
                continue;
            }

            if (profile is null)
            {
                _logger.LogWarning("User {UserId}: profile returned no data; skipping.", userId);
                continue;
            }

            var email = profile.Value.GetStringOrNull("email");
            if (TestRecordFilter.IsTestEmail(email))
            {
                _logger.LogInformation("User {UserId}: skipped, email looks like a test record.", userId);
                continue;
            }

            if (!profile.Value.TryGetProperty("applications", out var applications) ||
                applications.ValueKind != JsonValueKind.Array)
            {
                _logger.LogInformation("User {UserId}: no applications found.", userId);
                continue;
            }

            foreach (var application in applications.EnumerateArray())
            {
                var createdOn = application.GetStringOrNull("submission_date");
                if (string.IsNullOrEmpty(createdOn))
                {
                    _logger.LogInformation(
                        "User {UserId}, application {AppGuid}: skipped, still drafted (no submission_date).",
                        userId, application.GetStringOrNull("guid"));
                    continue; // still only drafted by user
                }

                if (!DateTime.TryParse(createdOn, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var submittedAt) ||
                    submittedAt < SubmittedOnCutoff)
                {
                    _logger.LogInformation(
                        "User {UserId}, application {AppGuid}: skipped, submitted before {Cutoff:yyyy-MM-dd}.",
                        userId, application.GetStringOrNull("guid"), SubmittedOnCutoff);
                    continue;
                }

                var campusGuid = application.TryGetProperty("campus", out var campusEl)
                    ? campusEl.GetStringOrNull("guid")
                    : null;

                if (!string.Equals(campusGuid, "lsua.campus.14510", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "User {UserId}, application {AppGuid}: skipped, campus {CampusGuid} is not online degree.",
                        userId, application.GetStringOrNull("guid"), campusGuid);
                    continue; // only online degree applications
                }

                var appGuid = application.GetStringOrNull("guid");
                if (!string.IsNullOrEmpty(appGuid) &&
                    await IsTestApplicationAsync(userId, appGuid, ct))
                {
                    continue;
                }

                var intendedTerm = application.TryGetProperty("term", out var termEl)
                    ? termEl.GetStringOrNull("name")
                    : null;

                string? code = null;
                var majorGuid = application.TryGetProperty("major", out var majorEl)
                    ? majorEl.GetStringOrNull("guid")
                    : null;

                if (!string.IsNullOrEmpty(majorGuid))
                {
                    code = await GetMajorCodeAsync(majorGuid, ct);
                }

                var hasDecision = application.TryGetProperty("decision", out var decision) &&
                                  decision.ValueKind == JsonValueKind.Object;
                if (!hasDecision)
                {
                    _logger.LogInformation(
                        "User {UserId}, application {AppGuid}: no decision yet.",
                        userId, application.GetStringOrNull("guid"));
                }

                var decisionId = hasDecision ? decision.GetStringOrNull("decision_id") : null;
                if (hasDecision && string.IsNullOrEmpty(decisionId))
                {
                    _logger.LogWarning(
                        "User {UserId}, application {AppGuid}: decision present but missing decision_id.",
                        userId, application.GetStringOrNull("guid"));
                }

                var checklist = new List<JsonElement>();
                if (!string.IsNullOrEmpty(decisionId))
                {
                    try
                    {
                        checklist = await _api.GetDecisionChecklistAsync(decisionId, ct);
                    }
                    catch (SegmentApiException ex)
                    {
                        _logger.LogError(
                            "Failed to load checklist for decision {DecisionId} (user {UserId}): {Message}",
                            decisionId, userId, ex.Message);
                    }
                }

                if (checklist.Count == 0)
                {
                    yield return new Dictionary<string, string?>
                    {
                        ["element_id"] = userId,
                        ["application_id"] = application.GetStringOrNull("registration_id"),
                        ["people_code_id"] = schoolId,
                        ["first_name"] = firstName,
                        ["middle_name"] = middleName,
                        ["last_name"] = lastName,
                        ["requirement_name"] = null,
                        ["created_on"] = JsonHelpers.ToCentralTimeString(createdOn),
                        ["requirement_status"] = null,
                        ["received_date"] = null,
                        ["code"] = code,
                        ["intended_term"] = intendedTerm,
                        ["email"] = email,
                    };
                    rowCount++;
                    continue;
                }

                foreach (var item in checklist)
                {
                    var status = item.GetStringOrNull("status");
                    if (string.Equals(status, "uncompleted", StringComparison.OrdinalIgnoreCase))
                    {
                        status = "incomplete";
                    }

                    yield return new Dictionary<string, string?>
                    {
                        ["element_id"] = userId,
                        ["application_id"] = application.GetStringOrNull("registration_id"),
                        ["people_code_id"] = schoolId,
                        ["first_name"] = firstName,
                        ["middle_name"] = middleName,
                        ["last_name"] = lastName,
                        ["requirement_name"] = item.GetStringOrNull("title"),
                        ["created_on"] = JsonHelpers.ToCentralTimeString(createdOn),
                        ["requirement_status"] = status,
                        ["received_date"] = (string.Equals(status, "received", StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                            ? JsonHelpers.ToCentralTimeString(item.GetStringOrNull("status_changed_at"))
                            : null,
                        ["code"] = code,
                        ["intended_term"] = intendedTerm,
                        ["email"] = email,
                    };
                    rowCount++;
                }
            }
        }

        _logger.LogInformation("Processed {UserCount} segment users, produced {RowCount} checklist rows.", userCount, rowCount);
    }

    /// <summary>
    /// Builds the analytics filter selecting users with the given application who were updated
    /// within the last 7 days (see <see cref="TestRecordFilter.RegisteredAtCutoff"/>) in any active term.
    /// </summary>
    private static object BuildFilter(string applicationGuid, IReadOnlyList<string> termGuids)
    {
        var termCondition = termGuids.Count == 1
            ? new
            {
                type = "filter",
                target = "<mapping:user-applications-term>",
                value = termGuids[0],
                @operator = "$eq",
            }
            : (object)new
            {
                @operator = "$or",
                conditions = termGuids.Select(guid => new
                {
                    type = "filter",
                    target = "<mapping:user-applications-term>",
                    value = guid,
                    @operator = "$eq",
                }),
            };

        return new
        {
            users = new
            {
                filters = new
                {
                    @operator = "$and",
                    conditions = new object[]
                    {
                        new
                        {
                            key = "application",
                            negative = false,
                            type = "metafilter",
                            properties = new
                            {
                                @operator = "$and",
                                conditions = new[]
                                {
                                    new
                                    {
                                        type = "filter",
                                        target = "<mapping:user-applications-guid>",
                                        value = applicationGuid,
                                        @operator = "$eq",
                                    },
                                },
                            },
                        },
                        new
                        {
                            @operator = "$and",
                            conditions = new object[]
                            {
                                new
                                {
                                    @operator = "$gte",
                                    value = TestRecordFilter.RegisteredAtCutoff.ToString("yyyy-MM-dd 00:00:00"),
                                    type = "filter",
                                    target = "<mapping:user-updated-at>",
                                },
                                new
                                {
                                    @operator = "$lte",
                                    value = DateTime.UtcNow.ToString("yyyy-MM-dd 23:59:59"),
                                    type = "filter",
                                    target = "<mapping:user-updated-at>",
                                },
                            },
                        },
                        new
                        {
                            @operator = "$and",
                            conditions = new object[] { termCondition },
                        },
                    },
                },
            },
            type = "interface",
            output = "users",
        };
    }

    /// <summary>
    /// True when the application's "user-applications-registered-at" predates go-live, i.e. it is
    /// leftover test data. An export failure is not treated as a test record.
    /// </summary>
    private async Task<bool> IsTestApplicationAsync(string userId, string applicationGuid, CancellationToken ct)
    {
        JsonElement? exported;
        try
        {
            exported = await _api.ExportUserFieldsAsync(userId, RegisteredAtColumns, applicationGuid, ct);
        }
        catch (SegmentApiException ex)
        {
            _logger.LogError(
                "User {UserId}, application {AppGuid}: failed to load registered-at ({Message}); keeping record.",
                userId, applicationGuid, ex.Message);
            return false;
        }

        if (!TestRecordFilter.IsTestRegistration(TestRecordFilter.ReadRegisteredAt(exported)))
            return false;

        _logger.LogInformation(
            "User {UserId}, application {AppGuid}: skipped, registered before {Cutoff:MM/dd/yyyy} (test data).",
            userId, applicationGuid, TestRecordFilter.RegisteredAtCutoff);
        return true;
    }

    private async Task<string?> GetMajorCodeAsync(string majorGuid, CancellationToken ct)
    {
        if (_majorCodeCache.TryGetValue(majorGuid, out var cached))
            return cached;

        string? code;
        try
        {
            var major = await _api.GetMajorAsync(majorGuid, ct);
            var rawCode = major.GetStringOrNull("code");
            code = rawCode is null ? null : rawCode.Replace(" ", "");
        }
        catch (SegmentApiException ex)
        {
            _logger.LogError("Failed to load major {MajorGuid}: {Message}", majorGuid, ex.Message);
            code = null;
        }

        _majorCodeCache[majorGuid] = code;
        return code;
    }

    private static string? GetOrNull(Dictionary<string, JsonElement> obj, string key) =>
        obj.TryGetValue(key, out var val) ? val.ToCsvValue() : null;
}
