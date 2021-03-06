﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using FluentMigrator;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Datastore.Migration.Framework;

namespace NzbDrone.Core.Datastore.Migration
{
    [Migration(70)]
    public class delay_profile : NzbDroneMigrationBase
    {
        protected override void MainDbUpgrade()
        {
            Create.TableForModel("DelayProfiles")
                  .WithColumn("EnableUsenet").AsBoolean().NotNullable()
                  .WithColumn("EnableTorrent").AsBoolean().NotNullable()
                  .WithColumn("PreferredProtocol").AsInt32().NotNullable()
                  .WithColumn("UsenetDelay").AsInt32().NotNullable()
                  .WithColumn("TorrentDelay").AsInt32().NotNullable()
                  .WithColumn("Order").AsInt32().NotNullable()
                  .WithColumn("Tags").AsString().NotNullable();

            Insert.IntoTable("DelayProfiles").Row(new
                                                  {
                                                      EnableUsenet = 1,
                                                      EnableTorrent = 1,
                                                      PreferredProtocol = 1,
                                                      UsenetDelay = 0,
                                                      TorrentDelay = 0,
                                                      Order = Int32.MaxValue,
                                                      Tags = "[]"
                                                  });

            Execute.WithConnection(ConvertProfile);

            Delete.Column("GrabDelay").FromTable("Profiles");
            Delete.Column("GrabDelayMode").FromTable("Profiles");
        }

        private void ConvertProfile(IDbConnection conn, IDbTransaction tran)
        {
            var profiles = GetProfiles(conn, tran);
            var order = 1;

            foreach (var profileClosure in profiles.DistinctBy(p => p.GrabDelay))
            {
                var profile = profileClosure;
                if (profile.GrabDelay == 0) continue;

                var tag = String.Format("delay-{0}", profile.GrabDelay);
                var tagId = InsertTag(conn, tran, tag);
                var tags = String.Format("[{0}]", tagId);

                using (IDbCommand insertDelayProfileCmd = conn.CreateCommand())
                {
                    insertDelayProfileCmd.Transaction = tran;
                    insertDelayProfileCmd.CommandText = "INSERT INTO DelayProfiles (EnableUsenet, EnableTorrent, PreferredProtocol, TorrentDelay, UsenetDelay, [Order], Tags) VALUES (1, 1, 1, 0, ?, ?, ?)";
                    insertDelayProfileCmd.AddParameter(profile.GrabDelay);
                    insertDelayProfileCmd.AddParameter(order);
                    insertDelayProfileCmd.AddParameter(tags);

                    insertDelayProfileCmd.ExecuteNonQuery();
                }

                var matchingProfileIds = profiles.Where(p => p.GrabDelay == profile.GrabDelay)
                                                 .Select(p => p.Id);

                UpdateSeries(conn, tran, matchingProfileIds, tagId);

                order++;
            }
        }

        private List<Profile70> GetProfiles(IDbConnection conn, IDbTransaction tran)
        {
            var profiles = new List<Profile70>();

            using (IDbCommand getProfilesCmd = conn.CreateCommand())
            {
                getProfilesCmd.Transaction = tran;
                getProfilesCmd.CommandText = @"SELECT Id, GrabDelay FROM Profiles";
                
                using (IDataReader profileReader = getProfilesCmd.ExecuteReader())
                {
                    while (profileReader.Read())
                    {
                        var id = profileReader.GetInt32(0);
                        var delay = profileReader.GetInt32(1);

                        profiles.Add(new Profile70
                        {
                            Id = id,
                            GrabDelay = delay * 60
                        });
                    }
                }
            }

            return profiles;
        }

        private Int32 InsertTag(IDbConnection conn, IDbTransaction tran, string tagLabel)
        {
            using (IDbCommand insertCmd = conn.CreateCommand())
            {
                insertCmd.Transaction = tran;
                insertCmd.CommandText = @"INSERT INTO Tags (Label) VALUES (?); SELECT last_insert_rowid()";
                insertCmd.AddParameter(tagLabel);

                var id = insertCmd.ExecuteScalar();

                return Convert.ToInt32(id);
            }
        }

        private void UpdateSeries(IDbConnection conn, IDbTransaction tran, IEnumerable<int> profileIds, int tagId)
        {
            using (IDbCommand getSeriesCmd = conn.CreateCommand())
            {
                getSeriesCmd.Transaction = tran;
                getSeriesCmd.CommandText = "SELECT Id, Tags FROM Series WHERE ProfileId IN (?)";
                getSeriesCmd.AddParameter(String.Join(",", profileIds));

                using (IDataReader seriesReader = getSeriesCmd.ExecuteReader())
                {
                    while (seriesReader.Read())
                    {
                        var id = seriesReader.GetInt32(0);
                        var tagString = seriesReader.GetString(1);

                        var tags = Json.Deserialize<List<int>>(tagString);
                        tags.Add(tagId);

                        using (IDbCommand updateSeriesCmd = conn.CreateCommand())
                        {
                            updateSeriesCmd.Transaction = tran;
                            updateSeriesCmd.CommandText = "UPDATE Series SET Tags = ? WHERE Id = ?";
                            updateSeriesCmd.AddParameter(tags.ToJson());
                            updateSeriesCmd.AddParameter(id);

                            updateSeriesCmd.ExecuteNonQuery();
                        }
                    }
                }

                getSeriesCmd.ExecuteNonQuery();
            }
        }

        private class Profile70
        {
            public int Id { get; set; }
            public int GrabDelay { get; set; }
        }

        private class Series70
        {
            public int Id { get; set; }
            public HashSet<int> Tags { get; set; }
        }
    }
}
