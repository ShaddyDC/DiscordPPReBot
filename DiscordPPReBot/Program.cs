using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Network;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Legacy;
using osu.Game.IO;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using Alba.CsConsoleFormat;
using Decoder = osu.Game.Beatmaps.Formats.Decoder;
using Document = Alba.CsConsoleFormat.Document;

namespace DiscordPPReBot
{
    public static class Program
    {
        private const string BaseUrl = "https://osu.ppy.sh";
        private static readonly string Key = Environment.GetEnvironmentVariable("API_KEY");

        private const int Ruleset = 0;

        public static void Main(string[] args)
        {
            MainAsync().GetAwaiter().GetResult();
        }

        private static async Task MainAsync()
        {
            var discord = new DiscordClient(new DiscordConfiguration()
            {
                Token = Environment.GetEnvironmentVariable("DISCORD_TOKEN"),
                TokenType = TokenType.Bot
            });

            discord.MessageCreated += async (_, e) =>
            {
                const string commandPrefix = "!profile ";
                if (!e.Message.Content.ToLower().StartsWith(commandPrefix)) return;

                var profile = e.Message.Content[commandPrefix.Length..];
                if (string.IsNullOrWhiteSpace(profile)) return;

                var message = await e.Message.RespondAsync("Getting User Data...");
                try
                {
                    var displayPlays = new List<UserPlayInfo>();
                    var ruleset = new OsuRuleset();

                    var userData = GetJsonFromApi($"get_user?k={Key}&u={profile}&m={Ruleset}")[0]; // TODO Error check

                    await message.ModifyAsync("Getting user top scores...");

                    var plays = GetJsonFromApi($"get_user_best?k={Key}&u={profile}&m={Ruleset}&limit=100");
                    foreach (var play in plays)
                    {
                        string beatmapId = play.beatmap_id;

                        var cachePath = Path.Combine("cache", $"{beatmapId}.osu");

                        if (!File.Exists(cachePath))
                        {
                            // This causes a lot of edits, which is rate limited by the discord api
                            // This implicitly also rate limits requests to the osu servers
                            await message.ModifyAsync($"Downloading {beatmapId}.osu...");
                            new FileWebRequest(cachePath, $"{BaseUrl}/osu/{beatmapId}").Perform();
                        }

                        Mod[] mods = ruleset.ConvertFromLegacyMods((LegacyMods) play.enabled_mods).ToArray();

                        var working = new ProcessorWorkingBeatmap(cachePath, (int) play.beatmap_id);

                        var scoreInfo = new ScoreInfo
                        {
                            Ruleset = ruleset.RulesetInfo,
                            TotalScore = play.score,
                            MaxCombo = play.maxcombo,
                            Mods = mods,
                            Statistics = new Dictionary<HitResult, int>()
                        };

                        scoreInfo.SetCount300((int) play.count300);
                        scoreInfo.SetCountGeki((int) play.countgeki);
                        scoreInfo.SetCount100((int) play.count100);
                        scoreInfo.SetCountKatu((int) play.countkatu);
                        scoreInfo.SetCount50((int) play.count50);
                        scoreInfo.SetCountMiss((int) play.countmiss);

                        var score = new ProcessorScoreDecoder(working).Parse(scoreInfo);

                        var performanceCalculator = ruleset.CreatePerformanceCalculator(working, score.ScoreInfo);

                        var thisPlay = new UserPlayInfo
                        {
                            Beatmap = working.BeatmapInfo,
                            LocalPp = performanceCalculator.Calculate(),
                            LivePp = play.pp,
                            Mods = mods.Length > 0
                                ? mods.Select(m => m.Acronym).Aggregate((c, n) => $"{c}, {n}")
                                : "None"
                        };

                        displayPlays.Add(thisPlay);
                    }

                    var localOrdered = displayPlays.OrderByDescending(p => p.LocalPp).ToList();
                    var liveOrdered = displayPlays.OrderByDescending(p => p.LivePp).ToList();

                    var index = 0;
                    var totalLocalPp = localOrdered.Sum(play => Math.Pow(0.95, index++) * play.LocalPp);
                    double totalLivePp = userData.pp_raw;

                    index = 0;
                    var nonBonusLivePp = liveOrdered.Sum(play => Math.Pow(0.95, index++) * play.LivePp);

                    //todo: implement properly. this is pretty damn wrong.
                    var playcountBonusPp = (totalLivePp - nonBonusLivePp);
                    totalLocalPp += playcountBonusPp;
                    var totalDiffPp = totalLocalPp - totalLivePp;

                    var document = new Document(
                        new Span($"User:     {userData.username}"), "\n",
                        new Span($"Live PP:  {totalLivePp:F1} (including {playcountBonusPp:F1}pp from playcount)"),
                        "\n",
                        new Span($"Local PP: {totalLocalPp:F1} ({totalDiffPp:+0.0;-0.0;-})"), "\n",
                        new Grid
                        {
                            Columns =
                            {
                                GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto,
                                GridLength.Auto
                            },
                            Children =
                            {
                                new Cell("beatmap"),
                                new Cell("mods"),
                                new Cell("live pp"),
                                new Cell("local pp"),
                                new Cell("pp change"),
                                new Cell("position change"),
                                localOrdered.Select(item => new[]
                                {
                                    new Cell($"{item.Beatmap.OnlineBeatmapID} - {item.Beatmap}"),
                                    new Cell(item.Mods),
                                    new Cell($"{item.LivePp:F1}") {Align = Align.Right},
                                    new Cell($"{item.LocalPp:F1}") {Align = Align.Right},
                                    new Cell($"{item.LocalPp - item.LivePp:F1}") {Align = Align.Right},
                                    new Cell($"{liveOrdered.IndexOf(item) - localOrdered.IndexOf(item):+0;-0;-}")
                                        {Align = Align.Center},
                                })
                            }
                        }
                    );

                    Stream stream;

                    await using (var writer = new StringWriter())
                    {
                        ConsoleRenderer.RenderDocumentToText(document, new TextRenderTarget(writer),
                            new Rect(0, 0, 250, Size.Infinity));
                        var bytes = Encoding.UTF8.GetBytes(writer.ToString());
                        stream = new MemoryStream(bytes);
                    }

                    var fileMessage = new DiscordMessageBuilder().WithFile($"{profile}.txt", stream);
                    await e.Message.RespondAsync(fileMessage);

                    await message.DeleteAsync();
                }
                catch (Exception ex)
                {
                    await message.ModifyAsync($"Failed with {ex}");
                }
            };

            await discord.ConnectAsync();
            await Task.Delay(-1);
        }

        private static dynamic GetJsonFromApi(string request)
        {
            using var req = new JsonWebRequest<dynamic>($"{BaseUrl}/api/{request}");
            req.Perform();
            return req.ResponseObject;
        }
    }

    internal class ProcessorScoreDecoder : LegacyScoreDecoder
    {
        private readonly WorkingBeatmap _beatmap;

        public ProcessorScoreDecoder(WorkingBeatmap beatmap)
        {
            this._beatmap = beatmap;
        }

        public Score Parse(ScoreInfo scoreInfo)
        {
            var score = new Score {ScoreInfo = scoreInfo};
            CalculateAccuracy(score.ScoreInfo);
            return score;
        }

        protected override Ruleset GetRuleset(int rulesetId) => new OsuRuleset();

        protected override WorkingBeatmap GetBeatmap(string md5Hash) => _beatmap;
    }

    internal class ProcessorWorkingBeatmap : WorkingBeatmap
    {
        private readonly Beatmap _beatmap;

        public ProcessorWorkingBeatmap(string file, int? beatmapId = null)
            : this(ReadFromFile(file), beatmapId)
        {
        }

        private ProcessorWorkingBeatmap(Beatmap beatmap, int? beatmapId = null)
            : base(beatmap.BeatmapInfo, null)
        {
            this._beatmap = beatmap;

            beatmap.BeatmapInfo.Ruleset = new OsuRuleset().RulesetInfo;

            if (beatmapId.HasValue)
                beatmap.BeatmapInfo.OnlineBeatmapID = beatmapId;
        }

        private static Beatmap ReadFromFile(string filename)
        {
            using var stream = File.OpenRead(filename);
            using var reader = new LineBufferedReader(stream);
            return Decoder.GetDecoder<Beatmap>(reader).Decode(reader);
        }

        protected override IBeatmap GetBeatmap() => _beatmap;
        protected override Texture GetBackground() => null;
        protected override Track GetBeatmapTrack() => null;
        public override Stream GetStream(string storagePath) => null;
    }

    internal class UserPlayInfo
    {
        public double LocalPp;
        public double LivePp;

        public BeatmapInfo Beatmap;

        public string Mods;
    }
}