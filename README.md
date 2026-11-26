# SmsForwardBot

The bot forwards incoming SMS messages, send and delivery reports, and system alerts from gsm-sip-bridge to a private Telegram chat. It queues outgoing SMS messages through the same WebUI API the browser uses.

`MironovLab.SmsForwardBot` runs the HTTP controller and the bot's background services. The project's `Bot` directory contains the bot logic and dependency registration through `Microsoft.Extensions.DependencyInjection`.

Outgoing SMS and send reports require a bridge build that includes commits `38ee3ea` and `4e01987` from the `feature/ims-sms-send` branch. Commit `51c4a9d` adds delivery reports and the `Category` and `Status` fields that the bot uses to identify notifications. The bot also supports the older webhook format. VoWiFi and VoLTE lines support sending SMS. GSM lines can receive SMS.

## Configuration

Fill in [appsettings.json](MironovLab.SmsForwardBot/appsettings.json) and restart the bot after changing its settings.

| Setting | Description |
| --- | --- |
| `Telegram:BotToken` | Bot token from [BotFather](https://core.telegram.org/bots/features#botfather) |
| `Telegram:OwnerUserId` | Your numeric Telegram user ID. The bot accepts commands only from this ID in a private chat |
| `Telegram:ApiBaseUrl` | Telegram Bot API URL. Defaults to `https://api.telegram.org/` |
| `Bridge:WebUiUrl` | WebUI URL reachable by the bot |
| `Bridge:WebUiPassword` | WebUI password |
| `DiscordWebhook:Secret` | A random string of 32–128 characters using Latin letters, digits, hyphens, and underscores |
| `DiscordWebhook:LogRawBody` | Logs the raw JSON webhook body at `Information` level after validating the secret. Set to `false` to disable |
| `Urls` | Address where the bot listens for webhooks. Defaults to HTTP on port 8090 |
| `Language` | Language of the bot's Telegram messages. Use `ru` for Russian (the default) or `en` for English |
| `Logging:LogLevel` | Microsoft.Extensions.Logging levels for the application and individual categories |
| `StateFile` | Path to the state file. When running with `dotnet run --project`, relative paths are resolved from the project directory |

The bot writes diagnostic logs in English. It reads `Language` at startup to determine the language of Telegram replies and new notifications. Queued notifications keep the text generated when the bot received them.

When `DiscordWebhook:LogRawBody` is enabled, the log includes SMS text and phone numbers from the request body.

If you move the installation to another directory, set `StateFile` to the absolute path of the existing `bot-state.json` to keep the selected line and queued notifications.

Enable WebUI and SMS in the bridge's `config.toml`. Replace `smsforwardbot` with the bot host's name or IP address and `YOUR_WEBHOOK_SECRET` with the value of `DiscordWebhook:Secret`.

```toml
[webui]
enabled = true
password = "env:WEBUI_PASSWORD"

[sms]
enabled = true
discord_webhook_url = "http://smsforwardbot:8090/webhooks/discord/YOUR_WEBHOOK_SECRET"
```

Use the same password in the bot's settings. To have the bridge request carrier delivery reports for new outgoing SMS messages, use a bridge build that supports them and add `request_status_reports = true` to its `[sms]` section. After restarting the bridge, start the bot with .NET SDK 10 or later and send `/start` in a private chat with the bot.

If your bridge build supports `[alerts]`, send system alerts to the same webhook. Enable the categories you want in the corresponding `[alerts.<category>]` sections. The following entries in the bridge's `config.toml` enable alerts when SIP registration is lost or restored.

```toml
[alerts]
discord_webhook_url = "http://smsforwardbot:8090/webhooks/discord/YOUR_WEBHOOK_SECRET"

[alerts.registration_loss]
enabled = true
```

If a category sets its own `discord_webhook_url`, point that section to the bot as well. Restart the bridge after changing its configuration.

```powershell
dotnet run --project MironovLab.SmsForwardBot
```

The bot receives Telegram commands through long polling. The bridge connects to the bot over HTTP to deliver webhooks. Use HTTPS for connections outside a trusted network. The state file temporarily stores the text of notifications waiting to be sent to Telegram. The state file, configured `appsettings.json`, and webhook URL contain personal data or secrets.

## Running as a service

For **systemd**, use [smsforwardbot.service](deploy/systemd/smsforwardbot.service). The example runs the `MironovLab.SmsForwardBot` executable from a single-file publish using `FolderProfile` for `linux-x64`. Put the published files and the configured `appsettings.json` in `/opt/smsforwardbot`. The server needs ASP.NET Core Runtime 10. Create a system user and group named `smsforwardbot` and give the service permission to read the application and configuration files and run the executable.

If you published the application as a DLL, set `ExecStart=/usr/bin/dotnet /opt/smsforwardbot/MironovLab.SmsForwardBot.dll`. [.NET deployment options](https://learn.microsoft.com/en-us/dotnet/core/deploying/)

On startup, systemd creates `/var/lib/smsforwardbot` for the state file and waits for the application to report that it is ready. When migrating an existing installation, set `Environment=StateFile=...` in the service file to the path of the existing `bot-state.json`. [.NET integration with systemd](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.hosting.systemdhostbuilderextensions.addsystemd?view=net-10.0)

On Linux, run these commands from the repository root to install and start the service.

```sh
sudo install -m 0644 deploy/systemd/smsforwardbot.service /etc/systemd/system/smsforwardbot.service
sudo systemctl daemon-reload
sudo systemctl enable --now smsforwardbot.service
```

For a **Windows service**, register the published `MironovLab.SmsForwardBot.exe` with the Service Control Manager. Place the configured `appsettings.json` next to the executable. [Deploying ASP.NET Core as a Windows service](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/windows-service?view=aspnetcore-10.0)

When running the bot as a service, set `StateFile` to an absolute path and grant the service account write access to the directory containing the file.

## Commands

In the owner's private chat, **Menu** by the message field lists the bot's commands. Choose **Show bot keyboard** there or send `/start` or `/help` to open the collapsible keyboard. Use the buttons or enter commands without arguments. The bot prompts for input one step at a time. Use `/cancel` to cancel the current action.

| Command | Action |
| --- | --- |
| `/send` | Prompts for the recipient's number and SMS text, then sends the message using the current line |
| `/reply` | Shows the sender's number from the last incoming SMS and prompts for a reply |
| `/lines` | Shows the lines detected by the bridge, the current selection mode, and buttons for selectable lines |
| `/line` | Lets you choose a VoWiFi or VoLTE line using a button or by entering its ID |
| `/auto` | Restores automatic line selection based on the last incoming SMS |
| `/cancel` | Cancels the action in progress |

In automatic mode, the bot checks the bridge's SMS history and the webhooks it has received. Before any SMS arrives, the bot uses the only line that supports sending if there is one, or prompts you to select a line manually. If the line that received the last SMS no longer supports sending, the bot asks you to choose another one. The manually selected line and queued notifications persist across restarts. After a restart, start any unfinished action again. Run one bot instance per token and state file.

For `/send` and `/reply`, the bot strips spaces, hyphens, and parentheses from the number. The resulting number must contain 3 to 20 digits and may start with a single `+`.

When `/reply` prompts for the reply text, it records the sender's number and the line that received the last incoming SMS. The line must support sending. Manual selection through `/line` applies to `/send`.

The bot first shows the SMS Outbox entry number. It then forwards a send report once the SMS center has accepted all parts or the bridge has finished its send attempts. If carrier delivery reports are enabled, the bot also forwards the final delivery status. The message may be fully delivered, partially delivered, undelivered, or marked with an unknown status if the carrier's report does not arrive in time. The SMS Outbox entry number in both notifications links them to the original `/send` or `/reply` command.
