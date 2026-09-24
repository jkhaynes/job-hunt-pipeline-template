using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using MimeKit;

namespace JobHunt;

public class GmailClient
{
    static readonly string[] Scopes = [GmailService.Scope.GmailModify, GmailService.Scope.GmailSend];
    readonly GmailService _svc;

    public GmailClient()
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = Env("GMAIL_CLIENT_ID"),
                ClientSecret = Env("GMAIL_CLIENT_SECRET"),
            },
            Scopes = Scopes,
        });
        var cred = new UserCredential(flow, "me", new TokenResponse { RefreshToken = Env("GMAIL_REFRESH_TOKEN") });
        _svc = new GmailService(new BaseClientService.Initializer { HttpClientInitializer = cred, ApplicationName = "job-hunt-pipeline" });
    }

    /// <summary>Returns (messageId, htmlBody, sentDate) for each unread alert.</summary>
    public async Task<List<(string Id, string Html, DateTimeOffset Date)>> UnreadAlertsAsync(string label)
    {
        var result = new List<(string, string, DateTimeOffset)>();
        var list = _svc.Users.Messages.List("me");
        list.Q = $"label:{label.Trim().Replace(' ', '-')} is:unread"; // Gmail search writes spaces in label names as hyphens
        list.MaxResults = 100;
        var page = await list.ExecuteAsync();
        foreach (var m in page.Messages ?? [])
        {
            var get = _svc.Users.Messages.Get("me", m.Id);
            get.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
            var raw = (await get.ExecuteAsync()).Raw;
            using var stream = new MemoryStream(FromBase64Url(raw));
            var mime = await MimeMessage.LoadAsync(stream);
            result.Add((m.Id, mime.HtmlBody ?? "", mime.Date));
        }
        return result;
    }

    public async Task MarkReadAsync(IEnumerable<string> ids)
    {
        var idList = ids.ToList();
        if (idList.Count == 0) return;
        await _svc.Users.Messages.BatchModify(
            new BatchModifyMessagesRequest { Ids = idList, RemoveLabelIds = ["UNREAD"] }, "me").ExecuteAsync();
    }

    /// <summary>Sends to DIGEST_TO only. The recipient never comes from model output.</summary>
    public async Task SendDigestAsync(string subject, string summary, string attachmentPath)
    {
        var me = Env("DIGEST_TO");
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(me));
        msg.To.Add(MailboxAddress.Parse(me));
        msg.Subject = subject;
        var body = new BodyBuilder { TextBody = summary };
        if (attachmentPath.Length > 0) body.Attachments.Add(attachmentPath);
        msg.Body = body.ToMessageBody();

        using var ms = new MemoryStream();
        await msg.WriteToAsync(ms);
        await _svc.Users.Messages.Send(new Message { Raw = ToBase64Url(ms.ToArray()) }, "me").ExecuteAsync();
    }

    /// <summary>One-time local helper: runs the installed-app OAuth flow and prints the refresh token.</summary>
    public static async Task PrintRefreshTokenAsync(string clientJsonPath)
    {
        var secrets = (await GoogleClientSecrets.FromFileAsync(clientJsonPath)).Secrets;
        var cred = await GoogleWebAuthorizationBroker.AuthorizeAsync(secrets, Scopes, "user", CancellationToken.None, new NullDataStore());
        Console.WriteLine($"GMAIL_CLIENT_ID={secrets.ClientId}");
        Console.WriteLine($"GMAIL_CLIENT_SECRET={secrets.ClientSecret}");
        Console.WriteLine($"GMAIL_REFRESH_TOKEN={cred.Token.RefreshToken}");
    }

    static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing env var {name}");

    static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    static string ToBase64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
