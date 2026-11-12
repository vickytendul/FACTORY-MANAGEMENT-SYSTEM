using FactoryManagementSystem.Entities;
using Npgsql;

namespace FactoryManagementSystem.Services.Trims
{
    /// The trims store's audits. Supabase only, with no Firestore
    /// counterpart and therefore no flag - this data has never lived
    /// anywhere else. Same arrangement as the feeding, down time and
    /// machine repositories.
    public sealed class TrimsAuditRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public TrimsAuditRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        private const string AuditCols = """
            select id, created_at, audit_day, started_by_code, started_by_name,
                   submitted_at, submitted_by_code, submitted_by_name, form_rev
            from public.trims_audits
            """;

        private static TrimsAudit ReadAudit(NpgsqlDataReader r) => new()
        {
            Id = (int)r.GetInt64(0),
            CreatedAt = r.GetDateTime(1),
            AuditDay = r.GetDateTime(2),
            StartedByCode = r.GetString(3),
            StartedByName = r.GetString(4),
            SubmittedAt = r.IsDBNull(5) ? null : r.GetDateTime(5),
            SubmittedByCode = r.IsDBNull(6) ? null : r.GetString(6),
            SubmittedByName = r.IsDBNull(7) ? null : r.GetString(7),
            FormRev = r.GetString(8),
        };

        private static TrimsAuditItem ReadItem(NpgsqlDataReader r) => new()
        {
            Id = (int)r.GetInt64(0),
            AuditId = (int)r.GetInt64(1),
            ItemKey = r.GetString(2),
            SectionNo = r.GetInt32(3),
            SectionName = r.GetString(4),
            ItemNo = r.GetInt32(5),
            ItemLabel = r.GetString(6),
            Result = r.GetString(7),
            Reason = r.IsDBNull(8) ? null : r.GetString(8),
            AnsweredAt = r.GetDateTime(9),
            AnsweredBy = r.GetString(10),
        };

        private const string ItemCols = """
            select id, audit_id, item_key, section_no, section_name,
                   item_no, item_label, result, reason, answered_at, answered_by
            from public.trims_audit_items
            """;

        /// One day's audit with its answers, or null when nobody has
        /// started one.
        public async Task<TrimsAudit?> GetForDayAsync(DateTime day)
        {
            await using var cmd = _dataSource.CreateCommand(
                AuditCols + " where audit_day = @d");
            cmd.Parameters.Add(new NpgsqlParameter("d", day.Date)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
            });

            TrimsAudit? audit;
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                audit = await r.ReadAsync() ? ReadAudit(r) : null;
            }
            if (audit == null) return null;

            audit.Items = await GetItemsAsync(audit.Id);
            audit.Photos = await GetPhotosAsync(audit.Id);
            return audit;
        }

        public async Task<TrimsAudit?> GetByIdAsync(int id)
        {
            await using var cmd = _dataSource.CreateCommand(
                AuditCols + " where id = @id");
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));

            TrimsAudit? audit;
            await using (var r = await cmd.ExecuteReaderAsync())
            {
                audit = await r.ReadAsync() ? ReadAudit(r) : null;
            }
            if (audit == null) return null;

            audit.Items = await GetItemsAsync(audit.Id);
            audit.Photos = await GetPhotosAsync(audit.Id);
            return audit;
        }

        public async Task<List<TrimsAuditItem>> GetItemsAsync(int auditId)
        {
            await using var cmd = _dataSource.CreateCommand(
                ItemCols + " where audit_id = @id order by section_no, item_no");
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)auditId));

            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<TrimsAuditItem>();
            while (await r.ReadAsync()) list.Add(ReadItem(r));
            return list;
        }

        /// Every audit between two days, newest first, each with its
        /// answers.
        ///
        /// The answers come back in one second query rather than one per
        /// audit: a month is a dozen audits and three hundred rows, and
        /// a round trip each would be a dozen round trips to a server
        /// that sleeps between visits.
        public async Task<List<TrimsAudit>> GetForRangeAsync(
            DateTime from, DateTime to)
        {
            var audits = new List<TrimsAudit>();

            await using (var cmd = _dataSource.CreateCommand(
                AuditCols + """
                 where audit_day between @from and @to
                 order by audit_day desc
                """))
            {
                cmd.Parameters.Add(new NpgsqlParameter("from", from.Date)
                {
                    NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
                });
                cmd.Parameters.Add(new NpgsqlParameter("to", to.Date)
                {
                    NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
                });

                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) audits.Add(ReadAudit(r));
            }

            if (audits.Count == 0) return audits;

            var byId = audits.ToDictionary(a => a.Id);
            await using (var cmd = _dataSource.CreateCommand(
                ItemCols + """
                 where audit_id = any(@ids)
                 order by section_no, item_no
                """))
            {
                cmd.Parameters.Add(new NpgsqlParameter("ids",
                    audits.Select(a => (long)a.Id).ToArray()));

                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var item = ReadItem(r);
                    if (byId.TryGetValue(item.AuditId, out var audit))
                        audit.Items.Add(item);
                }
            }

            return audits;
        }

        /// The last audit somebody actually finished.
        ///
        /// Submitted only: a half-walked audit started on Monday and
        /// abandoned is not a Monday audit, and letting it set the next
        /// due date would quietly buy three days nobody earned.
        ///
        /// Its answers come with it. The score is counted off them, so
        /// an audit read without them scores nought - which is what the
        /// status banner reported for a walk that had scored
        /// twenty-three.
        public async Task<TrimsAudit?> LastSubmittedAsync()
        {
            TrimsAudit? audit;
            await using (var cmd = _dataSource.CreateCommand(
                AuditCols + """
                 where submitted_at is not null
                 order by audit_day desc
                 limit 1
                """))
            {
                await using var r = await cmd.ExecuteReaderAsync();
                audit = await r.ReadAsync() ? ReadAudit(r) : null;
            }
            if (audit == null) return null;

            audit.Items = await GetItemsAsync(audit.Id);
            return audit;
        }

        /// Starts the day's audit, or hands back the one already there.
        ///
        /// One statement, so two people opening the page at the same
        /// moment end with one audit rather than one each and a
        /// collision. The unique index on audit_day is what the conflict
        /// clause lands on.
        public async Task<TrimsAudit> StartAsync(
            DateTime day, string byCode, string byName)
        {
            await using var cmd = _dataSource.CreateCommand("""
                insert into public.trims_audits
                    (audit_day, started_by_code, started_by_name, form_rev)
                values (@d, @code, @name, @rev)
                on conflict (audit_day) do update
                    set audit_day = excluded.audit_day
                returning id, created_at, audit_day, started_by_code,
                          started_by_name, submitted_at, submitted_by_code,
                          submitted_by_name, form_rev
                """);
            cmd.Parameters.Add(new NpgsqlParameter("d", day.Date)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
            });
            cmd.Parameters.Add(new NpgsqlParameter("code", byCode ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("name", byName ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("rev", TrimsChecklist.Revision));

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                throw new InvalidOperationException("The audit was not started.");
            return ReadAudit(r);
        }

        /// Writes one answer, replacing whatever was there.
        ///
        /// An auditor changes their mind halfway down a rack, so this is
        /// an upsert rather than an insert - and one statement, so two
        /// taps in the same second end at the second one rather than at
        /// a collision.
        ///
        /// Refused once the audit is submitted: the score has been
        /// signed, and an answer written underneath it afterwards would
        /// change a result somebody already put their name to.
        public async Task<bool> SetItemAsync(TrimsAuditItem item)
        {
            await using var cmd = _dataSource.CreateCommand("""
                insert into public.trims_audit_items
                    (audit_id, item_key, section_no, section_name, item_no,
                     item_label, result, reason, answered_by)
                select @audit, @key, @sec, @secName, @no, @label, @result,
                       @reason, @by
                where exists (
                    select 1 from public.trims_audits
                     where id = @audit and submitted_at is null
                )
                on conflict (audit_id, item_key) do update
                    set result = excluded.result,
                        reason = excluded.reason,
                        section_name = excluded.section_name,
                        item_label = excluded.item_label,
                        answered_at = now(),
                        answered_by = excluded.answered_by
                """);

            cmd.Parameters.Add(new NpgsqlParameter("audit", (long)item.AuditId));
            cmd.Parameters.Add(new NpgsqlParameter("key", item.ItemKey));
            cmd.Parameters.Add(new NpgsqlParameter("sec", item.SectionNo));
            cmd.Parameters.Add(new NpgsqlParameter("secName", item.SectionName ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("no", item.ItemNo));
            cmd.Parameters.Add(new NpgsqlParameter("label", item.ItemLabel ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("result", item.Result));
            cmd.Parameters.Add(
                new NpgsqlParameter("reason", (object?)item.Reason ?? DBNull.Value));
            cmd.Parameters.Add(new NpgsqlParameter("by", item.AnsweredBy ?? string.Empty));

            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        /// Takes one answer back off, so a row returns to not-checked.
        ///
        /// There is no third result to set it to: not checked IS the
        /// absence of the row, so undoing an answer means deleting it.
        public async Task<bool> ClearItemAsync(int auditId, string itemKey)
        {
            await using var cmd = _dataSource.CreateCommand("""
                delete from public.trims_audit_items
                 where audit_id = @audit
                   and item_key = @key
                   and exists (
                       select 1 from public.trims_audits
                        where id = @audit and submitted_at is null
                   )
                """);
            cmd.Parameters.Add(new NpgsqlParameter("audit", (long)auditId));
            cmd.Parameters.Add(new NpgsqlParameter("key", itemKey));
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        /// Signs the audit off.
        ///
        /// The count is checked inside the statement rather than read
        /// first, so an answer landing between the two cannot leave an
        /// audit submitted with a row still blank.
        public async Task<bool> SubmitAsync(
            int id, string byCode, string byName, int required)
        {
            await using var cmd = _dataSource.CreateCommand("""
                update public.trims_audits
                   set submitted_at = now(),
                       submitted_by_code = @code,
                       submitted_by_name = @name
                 where id = @id
                   and submitted_at is null
                   and (
                     select count(*) from public.trims_audit_items
                      where audit_id = @id
                   ) >= @required
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            cmd.Parameters.Add(new NpgsqlParameter("code", byCode ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("name", byName ?? string.Empty));
            cmd.Parameters.Add(new NpgsqlParameter("required", required));
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        // ── photographs ───────────────────────────────────────────────

        private const string PhotoCols = """
            select id, audit_id, item_key, content_type, size_bytes,
                   width, height, uploaded_at, uploaded_by
            from public.trims_audit_photos
            """;

        /// Everything but the bytes. A list that carried them would be
        /// megabytes before anybody had asked to look at one.
        private static TrimsAuditPhoto ReadPhoto(NpgsqlDataReader r) => new()
        {
            Id = (int)r.GetInt64(0),
            AuditId = (int)r.GetInt64(1),
            ItemKey = r.GetString(2),
            ContentType = r.GetString(3),
            SizeBytes = r.GetInt32(4),
            Width = r.IsDBNull(5) ? null : r.GetInt32(5),
            Height = r.IsDBNull(6) ? null : r.GetInt32(6),
            UploadedAt = r.GetDateTime(7),
            UploadedBy = r.GetString(8),
        };

        public async Task<List<TrimsAuditPhoto>> GetPhotosAsync(int auditId)
        {
            await using var cmd = _dataSource.CreateCommand(
                PhotoCols + " where audit_id = @id order by uploaded_at");
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)auditId));

            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<TrimsAuditPhoto>();
            while (await r.ReadAsync()) list.Add(ReadPhoto(r));
            return list;
        }

        /// One photograph, bytes and all. The only call that reads them.
        public async Task<TrimsAuditPhoto?> GetPhotoAsync(int id)
        {
            await using var cmd = _dataSource.CreateCommand("""
                select id, audit_id, item_key, content_type, size_bytes,
                       width, height, uploaded_at, uploaded_by, bytes
                from public.trims_audit_photos
                where id = @id
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));

            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            var photo = ReadPhoto(r);
            photo.Bytes = (byte[])r[9];
            return photo;
        }

        public async Task<int> CountPhotosAsync(int auditId, string itemKey)
        {
            await using var cmd = _dataSource.CreateCommand("""
                select count(*) from public.trims_audit_photos
                 where audit_id = @id and item_key = @key
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)auditId));
            cmd.Parameters.Add(new NpgsqlParameter("key", itemKey));
            var n = await cmd.ExecuteScalarAsync();
            return n is long v ? (int)v : 0;
        }

        /// Writes a photograph, but only onto an audit still open.
        ///
        /// The check is in the statement rather than in a read before
        /// it, so a sign-off landing while somebody is uploading cannot
        /// put a picture onto a result that has been signed.
        public async Task<TrimsAuditPhoto?> AddPhotoAsync(TrimsAuditPhoto photo)
        {
            await using var cmd = _dataSource.CreateCommand("""
                insert into public.trims_audit_photos
                    (audit_id, item_key, content_type, bytes, size_bytes,
                     width, height, uploaded_by)
                select @audit, @key, @type, @bytes, @size, @w, @h, @by
                where exists (
                    select 1 from public.trims_audits
                     where id = @audit and submitted_at is null
                )
                returning id, audit_id, item_key, content_type, size_bytes,
                          width, height, uploaded_at, uploaded_by
                """);

            cmd.Parameters.Add(new NpgsqlParameter("audit", (long)photo.AuditId));
            cmd.Parameters.Add(new NpgsqlParameter("key", photo.ItemKey));
            cmd.Parameters.Add(new NpgsqlParameter("type", photo.ContentType));
            cmd.Parameters.Add(new NpgsqlParameter("bytes", photo.Bytes ?? [])
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea,
            });
            cmd.Parameters.Add(new NpgsqlParameter("size", photo.SizeBytes));
            cmd.Parameters.Add(
                new NpgsqlParameter("w", (object?)photo.Width ?? DBNull.Value)
                {
                    NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer,
                });
            cmd.Parameters.Add(
                new NpgsqlParameter("h", (object?)photo.Height ?? DBNull.Value)
                {
                    NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer,
                });
            cmd.Parameters.Add(new NpgsqlParameter("by", photo.UploadedBy ?? string.Empty));

            await using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? ReadPhoto(r) : null;
        }

        /// Removes a photograph, while the audit is still open.
        ///
        /// A signed audit keeps its pictures. They are part of what was
        /// signed, and taking one off afterwards would change the
        /// record somebody put their name to.
        public async Task<bool> DeletePhotoAsync(int id)
        {
            await using var cmd = _dataSource.CreateCommand("""
                delete from public.trims_audit_photos p
                 using public.trims_audits a
                 where p.id = @id
                   and a.id = p.audit_id
                   and a.submitted_at is null
                """);
            cmd.Parameters.Add(new NpgsqlParameter("id", (long)id));
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        /// Every not-OK answer over a period, newest first.
        ///
        /// What the history page is for: one cross is an incident, the
        /// same cross three audits running is the thing somebody has to
        /// go and fix, and only a list across audits can tell them
        /// apart.
        public async Task<List<TrimsAuditItem>> NotOkSinceAsync(DateTime from)
        {
            await using var cmd = _dataSource.CreateCommand("""
                select i.id, i.audit_id, i.item_key, i.section_no, i.section_name,
                       i.item_no, i.item_label, i.result, i.reason,
                       i.answered_at, i.answered_by
                from public.trims_audit_items i
                join public.trims_audits a on a.id = i.audit_id
                where i.result = 'not_ok'
                  and a.audit_day >= @from
                order by i.answered_at desc
                """);
            cmd.Parameters.Add(new NpgsqlParameter("from", from.Date)
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date,
            });

            await using var r = await cmd.ExecuteReaderAsync();
            var list = new List<TrimsAuditItem>();
            while (await r.ReadAsync()) list.Add(ReadItem(r));
            return list;
        }
    }
}
