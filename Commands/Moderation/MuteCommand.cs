using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Linq;

using Discord;
using Discord.Commands;

using BrackeysBot.Modules;

namespace BrackeysBot.Commands.Moderation
{
    public class MuteCommand : ModuleBase
    {
        private readonly MuteTable _mutes;
        private readonly AuditLog _auditLog;

        public MuteCommand(MuteTable mutes, AuditLog log)
        {
            _mutes = mutes;
            _auditLog = log;
        }

        [Command("tempmute")]
        [PermissionRestriction(UserType.Staff)]
        [HelpData("tempmute <member> <duration in hours> <reason> (optional)", "Mute a member for the specified amount of time.")]
        public async Task TempMute(IGuildUser user, double duration, [Optional] [Remainder] string reason)
        {
            // Converts from plain text to text with special characters
            reason = CommandConversion.FromConverted(reason);

            _mutes.Set(user.Id.ToString() + "," + Context.Guild.Id.ToString(), (DateTime.UtcNow + new TimeSpan((long)(duration * TimeSpan.TicksPerHour))).ToBinary().ToString());
            string _displayName = user.GetDisplayName();

            await user.Mute();

            IMessage messageToDel = await ReplyAsync($":white_check_mark: Successfully muted {_displayName} for {duration} hours.");
            _ = messageToDel.TimedDeletion(3000);
            await Context.Message.DeleteAsync();
            
            await _auditLog.AddEntry($"{_displayName} was muted for {duration} hours.");
        }

        [Command("mute")]
        [PermissionRestriction(UserType.Staff)]
        [HelpData("mute <member> <reason> (optional)", "Mute a member.")]
        public async Task Mute(IGuildUser user, [Optional] [Remainder] string reason)
        {
            // Converts from plain text to text with special characters
            reason = CommandConversion.FromConverted(reason);

            _mutes.Remove(user.Id.ToString() + "," + Context.Guild.Id.ToString());
            string _displayName = user.GetDisplayName();

            await user.Mute();

            IMessage messageToDel = await ReplyAsync($":white_check_mark: Successfully muted {_displayName}.");
            _ = messageToDel.TimedDeletion(3000);
            await Context.Message.DeleteAsync();

            await _auditLog.AddEntry($"{_displayName} was muted.");
        }

        [Command("unmute")]
        [PermissionRestriction(UserType.Staff)]
        [HelpData("unmute <member>", "Mute a member.")]
        public async Task Unmute(IGuildUser user)
        {
            _mutes.Remove(user.Id.ToString() + "," + Context.Guild.Id.ToString());
            string _displayName = user.GetDisplayName();

            await user.Unmute();

            IMessage messageToDel = await ReplyAsync($":white_check_mark: Successfully unmuted {_displayName}.");
            _ = messageToDel.TimedDeletion(3000);
            await Context.Message.DeleteAsync();

            await _auditLog.AddEntry($"{_displayName} was unmuted.");
        }
    }
}
