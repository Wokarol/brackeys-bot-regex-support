using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Discord;
using Discord.WebSocket;

using BrackeysBot.Commands;
using BrackeysBot.Modules;

namespace BrackeysBot
{
    public sealed class BrackeysBot
    {
        public static IConfiguration Configuration { get; set; }
        public const int Version = 3;

        public DataModule Data { get; set; }
        public CommandHandler Commands { get; set; }

        private IServiceProvider _services;
        private DiscordSocketClient _client;

        private EventPointCommand.LeaderboardNavigator _leaderboardNavigator;
        private VideoSuggestionsHandler _videoSuggestionsHandler;
        private AuditLog _auditLog;

        /// <summary>
        /// Creates a new instance of the bot and initializes the configuration.
        /// </summary>
        public BrackeysBot()
        {
            IConfigurationBuilder builder = new ConfigurationBuilder()
               .SetBasePath(Directory.GetCurrentDirectory())
               .AddJsonFile("appsettings.json");

            Configuration = builder.Build();
        }

        /// <summary>
        /// Starts the execution of the bot.
        /// </summary>
        public async Task Start()
        {
            Log.Initialize();
            Log.Settings = new Log.LogSettings { IncludeTimestamp = true };

            Console.WriteLine($"\n  [ ] BrackeysBot v{Version}\n");
            Log.WriteLine("=== Intializing Startup ===");

            _client = new DiscordSocketClient();
            _client.Log += async logMessage => Log.WriteLine($"[DiscordClient] ({logMessage.Severity.ToString()}) {logMessage.Message}");
            _client.Ready += OnReady;
            _client.UserJoined += OnUserJoinedServer;

            Data = new DataModule();
            Data.InitializeDataFiles();

            Commands = new CommandHandler(Data, Configuration["prefix"], Configuration["ignore_cc_prefix"]);

            _leaderboardNavigator = new EventPointCommand.LeaderboardNavigator(Data.EventPoints, Data.Settings);
            _auditLog = new AuditLog();

            _services = new ServiceCollection()

                // Add BrackeysBot
                .AddSingleton(this)

                // Add the command service
                .AddSingleton(Commands.Service)

                // Add the singletons for the databases
                .AddSingleton(Data.EventPoints)
                .AddSingleton(Data.Settings)
                .AddSingleton(Data.Statistics)
                .AddSingleton(Data.CustomCommands)
                .AddSingleton(Data.Cooldowns)
                .AddSingleton(Data.Rules)
                .AddSingleton(Data.UnityDocs)
                .AddSingleton(Data.Mutes)
                .AddSingleton(Data.Bans)

                .AddSingleton(_leaderboardNavigator)
                .AddSingleton(_auditLog)

                // Finally, build the provider
                .BuildServiceProvider();

            UserHelper.Data = Data;
            
            Commands.ServiceProvider = _services;
            await Commands.InstallCommands(_client);
            
            RegisterMuteOnJoin();
            RegisterMassiveCodeblockHandle();
            RegisterMentionMessage();
            RegisterStaffPingLogging();
            RegisterLeaderboardNavigationHandle();

            await _client.LoginAsync(TokenType.Bot, Configuration["token"]);
            await _client.SetGameAsync($"{ Configuration["prefix"] }help");
            await _client.StartAsync();
        }

        /// <summary>
        /// Asynchronously logs out the bot. Also terminates the application, if specified.
        /// </summary>
        public async Task ShutdownAsync(bool terminate)
        {
            await _client.LogoutAsync();
            if (terminate)
            {
                Environment.Exit(0);
            }
        }

        private async Task OnReady ()
        {
            // This means that the bot updated last time, so send a message and delete the file
            if (File.Exists (Path.Combine (Directory.GetCurrentDirectory (), "updated.txt")))
            {
                string [] contents = ( await File.ReadAllTextAsync (Path.Combine (Directory.GetCurrentDirectory (), "updated.txt"))).Split ('\n');
                SocketGuild guild = _client.GetGuild (ulong.Parse (contents [0]));
                SocketGuildChannel channel = guild.GetChannel (ulong.Parse (contents [1]));
                await ((IMessageChannel) channel).SendMessageAsync ("Successfully updated!");
                File.Delete (Path.Combine (Directory.GetCurrentDirectory (), "updated.txt"));
            }

            // Intialize the audit log
            if (Data.Settings.Has("server-id") && Data.Settings.Has("log-channel-id"))
            {
                ulong guildId = ulong.Parse(Data.Settings["server-id"]);
                ulong logChannelId = ulong.Parse(Data.Settings["log-channel-id"]);

                IGuild mainGuild = _client.GetGuild(guildId);
                IMessageChannel auditLogChannel = await mainGuild.GetTextChannelAsync(logChannelId);

                _auditLog.Channel = auditLogChannel;
            }
            else
            {
                Log.WriteLine("Can't initialize the video settings handler: Either the server ID or the log channel ID has not been assigned.");
            }

            _ = PeriodicCheckMute(new TimeSpan(TimeSpan.TicksPerHour * 1), CancellationToken.None);
            _ = PeriodicCheckBan(new TimeSpan(TimeSpan.TicksPerHour * 1), CancellationToken.None);
            _ = PeriodicCheckVideoSuggestions(new TimeSpan(TimeSpan.TicksPerHour * 4), CancellationToken.None);
        }

        private async Task OnUserJoinedServer(SocketGuildUser arg)
        {
            if (!Data.Settings.Has("greeting-message"))
                return;

            try
            {
                string message = Data.Settings.Get("greeting-message");
                message = message.Replace("[user]", arg.Username);
                await arg.SendMessageAsync(message);

                Log.WriteLine($"User {arg.Id} was greeted in his DMs!");
            }
            catch (Exception ex)
            {
                Log.WriteLine($"I attempted to greet User {arg.Id} in his DMs but something went wrong.\n{ex.ToString()}");
            }
        }

        /// <summary>
        /// Registers a method to handle massive codeblocks.
        /// </summary>
        private void RegisterMassiveCodeblockHandle()
        {
            _client.MessageReceived += HandleMassiveCodeblock;
        }
        /// <summary>
        /// Registers a method that is called once the bot is mentioned.
        /// </summary>
        private void RegisterMentionMessage()
        {
            _client.MessageReceived += async (s) =>
            {
                if (!(s is SocketUserMessage msg)) return;

                string mention = _client.CurrentUser.Mention.Replace("!", "");
                if (msg.Content.StartsWith(mention) && msg.Content.Length == mention.Length)
                {
                    await msg.Channel.SendMessageAsync($"The command prefix for this server is `{ Configuration["prefix"] }`!");
                    return;
                }
            };
        }
        /// <summary>
        /// Registers a method that will log messages that include staff mentions.
        /// </summary>
        private void RegisterStaffPingLogging()
        {
            _client.MessageReceived += async (s) =>
            {
                if (!(s is SocketUserMessage msg) || s.Author.IsBot) { return; }
                if (!Data.Settings.Has("staff-role")) { return; }

                SocketGuild guild = (msg.Channel as SocketGuildChannel).Guild;
                SocketRole staffRole = guild.Roles.FirstOrDefault(r => r.Name == Data.Settings.Get("staff-role"));
                if(staffRole != null && s.MentionedRoles.Contains(staffRole))
                {
                    string author = msg.Author.Mention;
                    string messageLink = $@"https://discordapp.com/channels/{ guild.Id }/{ msg.Channel.Id }/{ msg.Id }";
                    string messageContent = msg.Content.Replace(staffRole.Mention, "@" + staffRole.Name);

                    await _auditLog.AddEntry($"{ author } mentioned staff in the following message! (<{ messageLink }>)\n```\n{ messageContent }\n```");
                    Log.WriteLine($"{author} mentioned @Staff in the following message: {messageLink}");
                }
            };
        }

        /// <summary>
        /// Registers a method to mute people who were muted but decided to be clever and wanted to rejoin to lose the muted role.
        /// </summary>
        private void RegisterMuteOnJoin()
        {
            _client.UserJoined += CheckMuteOnJoin;
        }

        private async Task CheckMuteOnJoin(SocketGuildUser user)
        {
            if (DateTime.UtcNow.ToBinary() < user.GetMuteTime()) { await user.Mute(); }
            else { await user.Unmute(); }
        }

        public async Task PeriodicCheckMute(TimeSpan interval, CancellationToken cancellationToken)
        {
            while (true)
            {
                Parallel.For(0, Data.Mutes.Mutes.Count,
                   async index =>
                   {
                       try
                       {
                           var current = Data.Mutes.Mutes.ElementAt(index);
                           if (DateTime.UtcNow.ToBinary() >= long.Parse(current.Value))
                           {
                               SocketGuild guild = _client.GetGuild(ulong.Parse(current.Key.Split(',')[1]));
                               SocketGuildUser user = guild.GetUser(ulong.Parse(current.Key.Split(',')[0]));
                               await user.Unmute();
                               Data.Mutes.Remove(current.Key);
                           }
                       }
                       catch { }
                   });
                Log.WriteLine("Checked mutes.");
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task PeriodicCheckBan(TimeSpan interval, CancellationToken cancellationToken)
        {
            while (true)
            {
                Parallel.For(0, Data.Bans.Bans.Count,
                   async index =>
                   {
                       try
                       {
                           var current = Data.Bans.Bans.ElementAt(index);
                           if (DateTime.UtcNow.ToBinary() >= long.Parse(current.Value))
                           {
                               SocketGuild guild = _client.GetGuild(ulong.Parse(current.Key.Split(',')[1]));
                               IUser user = null;
                               foreach (IBan ban in await guild.GetBansAsync())
                               {
                                   if (ban.User.Id == ulong.Parse(current.Key.Split(',')[0]))
                                   {
                                       user = ban.User;
                                   }
                               }
                               await guild.RemoveBanAsync(user);
                               Data.Bans.Remove(current.Key);
                           }
                       }
                       catch { }
                   });
                Log.WriteLine("Checked bans.");
                await Task.Delay(interval, cancellationToken);
            }
        }

        public async Task PeriodicCheckVideoSuggestions(TimeSpan interval, CancellationToken cancellationToken)
        {
            if (!Data.Settings.Has("server-id"))
            {
                Log.WriteLine("Can't initialize the video settings handler: The setting 'server-id' has not been assigned.");
                return;
            }
            
            ulong guildId = ulong.Parse(Data.Settings["server-id"]);
            IGuild mainGuild = _client.GetGuild(guildId);

            _videoSuggestionsHandler = new VideoSuggestionsHandler(mainGuild);
            await _videoSuggestionsHandler.InitializeAsync();

            while (true)
            {
                bool valid = _videoSuggestionsHandler.IsChannelStateValid();
                if (!valid)
                {
                    await _videoSuggestionsHandler.UpdateChannelStateToValid();
                }

                string FormatValid(bool v) => $"'{(v ? "Valid" : "Invalid")}'";

                Log.WriteLine($"Checked VideoSuggestions state and transitioned from state {FormatValid(valid)} to state {FormatValid(_videoSuggestionsHandler.IsChannelStateValid())}.");
                await Task.Delay(interval, cancellationToken);
            }
        }

        /// <summary>
        /// Registers the handle for a leaderboard navigation event.
        /// </summary>
        private void RegisterLeaderboardNavigationHandle()
        {
            _client.ReactionAdded += _leaderboardNavigator.HandleLeaderboardNavigation;
        }

        /// <summary>
        /// Handles a massive codeblock.
        /// </summary>
        private async Task HandleMassiveCodeblock(SocketMessage s)
        {
            if (!(s is SocketUserMessage msg) || msg.Author.IsBot) return;

            // Ignore specific channels
            if (Data.Settings.Has("job-channel-ids"))
            {
                ulong[] ignoreChannelIds = Data.Settings.Get("job-channel-ids").Split(',').Select(id => ulong.Parse(id.Trim())).ToArray();
                if (ignoreChannelIds.Any(id => id == s.Channel.Id)) return;
            }

            await PasteCommand.PasteIfMassiveCodeblock(s);
        }
    }
}
