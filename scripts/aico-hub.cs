#:project ../src/Cnu.AICO.Core/Cnu.AICO.Core.csproj
#:property PublishAot=true
#:property InvariantGlobalization=true
#:property OptimizationPreference=Size
// aico-hub: the local overview page (issues, agents, to-do's). Runs on the HOST only, never in a sandbox or in CI.
//   dotnet run scripts/aico-hub.cs [-- --repo <checkout>]                     (needs .NET; for development)
//   dotnet publish scripts/aico-hub.cs -r win-x64 -o <dir>                      (Native AOT single file; no .NET on the host)
// Then open http://127.0.0.1:5171 (Claude desktop: preview "aico-hub" from .claude/launch.json).
// Environment: GITHUB_TOKEN (read issues/PRs; never logged), SONARQUBE_HOME (default ~/Documents/Tools/Sonarqube).
// Everything the hub decides lives in Cnu.AICO.Core.Ops.LocalHub (tested); this file is I/O only.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cnu.AICO.Core.Ops;

const int MaxEventBytes = 64 * 1024;
const int MaxBrowserBytes = 8 * 1024;
var port = HubDefaults.Port;
var json = HubJsonContext.Default;

string? Opt(string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : null;

// ---------------------------------------------------------------- startup: repo, token, stores
var repoDir = Path.GetFullPath(Opt("--repo") ?? Directory.GetCurrentDirectory());
var notices = new ConcurrentDictionary<string, string>();
var top = await Run("git", ["-C", repoDir, "rev-parse", "--show-toplevel"], null, TimeSpan.FromSeconds(10));
if (top.Code == 0)
    repoDir = Path.GetFullPath(top.Out.Trim());
else
    notices["repo"] = $"{repoDir} is not a git checkout (or git is missing). Start aico-hub from the repository checkout, or pass --repo <path>.";
var remote = await Run("git", ["-C", repoDir, "remote", "get-url", "origin"], null, TimeSpan.FromSeconds(10));
var repo = remote.Code == 0 ? GitHubData.ParseRemote(remote.Out) : null;
if (repo is null && top.Code == 0)
    notices["remote"] = "The 'origin' remote is not a GitHub repository; issues are not shown.";
var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
if (string.IsNullOrWhiteSpace(token))
    notices["token"] = "GITHUB_TOKEN is not set, so issues and issue-* PRs are not shown. Create a fine-grained token with read access to " +
                       "Issues and Pull requests of this repository only, set GITHUB_TOKEN in the environment, and restart aico-hub.";
if (!File.Exists(Path.Combine(repoDir, "sbxenv.yaml")))
    notices["sbxenv"] = $"No sbxenv.yaml in {repoDir}: the Plan button needs the repository checkout.";

var csrf = HubGuard.NewToken();
var nonce = HubGuard.NewToken()[..32];
var tracker = new HubTracker();
var todos = new PersonalTodoStore(PersonalTodoStore.DefaultPath());
var sonarLog = Path.Combine(Path.GetDirectoryName(todos.Path)!, "sonarqube.log");
var page = Page.Html.Replace("{{TOKEN}}", csrf).Replace("{{NONCE}}", nonce);

IReadOnlyList<HubIssue> issues = [];
IReadOnlyList<HubPull> pulls = [];
IReadOnlyList<SandboxStatus> sandboxes = [];
var activity = new ConcurrentDictionary<string, string>();
var busy = new ConcurrentDictionary<string, bool>();

using var github = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(20) };
github.DefaultRequestHeaders.UserAgent.ParseAdd("aico-hub");
github.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
github.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

// ---------------------------------------------------------------- background refresh (sbx every 10 s, GitHub every 60 s)
_ = Task.Run(async () =>
{
    var nextGitHub = DateTimeOffset.MinValue;
    while (true)
    {
        await RefreshSandboxes();
        if (DateTimeOffset.UtcNow >= nextGitHub)
        {
            await RefreshGitHub();
            nextGitHub = DateTimeOffset.UtcNow.AddSeconds(60);
        }
        await Task.Delay(TimeSpan.FromSeconds(10));
    }
});

// ---------------------------------------------------------------- HTTP
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Prefixes.Add($"http://localhost:{port}/");
try
{
    listener.Start();
}
catch (HttpListenerException ex)
{
    Console.Error.WriteLine($"aico-hub: cannot listen on port {port}: {ex.Message}");
    return 1;
}
Console.WriteLine($"aico-hub on http://127.0.0.1:{port}  repo={repo ?? "(none)"}  checkout={repoDir}  GITHUB_TOKEN={(string.IsNullOrWhiteSpace(token) ? "missing" : "set")}");
Console.CancelKeyPress += (_, e) => { e.Cancel = true; listener.Stop(); };

while (listener.IsListening)
{
    HttpListenerContext ctx;
    try
    {
        ctx = await listener.GetContextAsync();
    }
    catch (Exception) when (!listener.IsListening)
    {
        break;
    }
    catch (HttpListenerException)
    {
        continue;
    }
    _ = Task.Run(async () =>
    {
        try
        {
            await Handle(ctx);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"aico-hub: {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath}: {ex.GetType().Name}: {ex.Message}");
            try { await Send(ctx.Response, 500, Result(false, "Internal error; see the hub console.")); } catch { /* connection gone */ }
        }
    });
}
return 0;

async Task Handle(HttpListenerContext ctx)
{
    var req = ctx.Request;
    var res = ctx.Response;
    var path = req.Url?.AbsolutePath ?? "/";
    var method = req.HttpMethod;
    var host = req.Headers["Host"] ?? "";
    var fromSandbox = method == "POST" && path == "/events";

    // Loopback only. Sandboxes arrive through the sbx host proxy (a local process) as host.docker.internal.
    if (req.RemoteEndPoint is null || !IPAddress.IsLoopback(req.RemoteEndPoint.Address) || !HostAllowed(host, fromSandbox))
    {
        await Send(res, 403, Result(false, "Forbidden."));
        return;
    }

    if (fromSandbox)
    {
        if (!HubGuard.EventRequestAllowed(req.Headers["Origin"], req.ContentType))
        {
            await Send(res, 403, Result(false, "Events must be JSON and must not come from a browser."));
            return;
        }
        var body = await ReadBody(req, MaxEventBytes);
        if (body is null)
        {
            await Send(res, 413, Result(false, "Event too large."));
            return;
        }
        HubEvent? ev = null;
        try { ev = JsonSerializer.Deserialize(body, json.HubEvent); } catch (JsonException) { }
        var ok = ev is not null && tracker.Apply(ev, DateTimeOffset.UtcNow);
        res.StatusCode = ok ? 204 : 400;
        res.Close();
        return;
    }

    if (method == "GET" && path == "/")
    {
        res.Headers["Content-Security-Policy"] =
            $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'";
        await SendText(res, 200, "text/html; charset=utf-8", page);
        return;
    }
    if (method == "GET" && path == "/api/state")
    {
        await SendText(res, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(State(), json.HubStateResponse));
        return;
    }
    if (method is not ("POST" or "PUT" or "DELETE"))
    {
        await Send(res, 404, Result(false, "Not found."));
        return;
    }

    // Browser actions: the page's own origin + the per-launch CSRF token + a JSON body.
    if (!HubGuard.BrowserPostAllowed(req.Headers["Origin"], req.Headers["X-AICO-Token"], csrf) || !HubGuard.IsJson(req.ContentType))
    {
        await Send(res, 403, Result(false, "Missing or wrong origin/token; reload the page."));
        return;
    }
    var input = await ReadBody(req, MaxBrowserBytes);
    if (input is null)
    {
        await Send(res, 413, Result(false, "Request too large."));
        return;
    }
    var parts = path.Trim('/').Split('/');
    var (status, result) = (method, parts) switch
    {
        ("POST", ["api", "issues", var n, "plan"]) when int.TryParse(n, out var number) => Plan(number),
        ("POST", ["api", "sandboxes", var name, "stop"]) => Stop(Uri.UnescapeDataString(name)),
        ("POST", ["api", "sonarqube", "start"]) => StartSonar(),
        ("POST", ["api", "todos"]) => AddTodo(input),
        ("PUT", ["api", "todos", var id]) => UpdateTodo(id, input),
        ("DELETE", ["api", "todos", var id]) => todos.Delete(id) ? (200, Result(true, "Deleted.")) : (404, Result(false, "No such to-do.")),
        _ => (404, Result(false, "Not found.")),
    };
    await Send(res, status, result);
}

bool HostAllowed(string host, bool fromSandbox) =>
    host == $"127.0.0.1:{port}" || host == $"localhost:{port}" || (fromSandbox && host == $"host.docker.internal:{port}");

// ---------------------------------------------------------------- actions
(int, HubActionResult) Plan(int number)
{
    var issue = issues.FirstOrDefault(i => i.Number == number);
    if (issue is null)
        return (404, Result(false, $"#{number} is not an open issue (yet); wait for the next refresh."));
    if (!issue.PlanAllowed)
        return (403, Result(false, $"#{number} is critical: human only."));
    var name = HubCommands.SandboxName(number);
    if (sandboxes.Any(s => s.Name == name))
        return (409, Result(false, $"{name} already exists. Attach: {HubCommands.AttachCommand(number)}"));
    if (!busy.TryAdd(name, true))
        return (409, Result(false, $"{name} is already being started."));
    var commands = HubCommands.PlanCommands(number, repoDir, issue.Labels);
    _ = RunSequence(name, $"Plan #{number}", commands, $"agent started in plan mode. Attach: {HubCommands.AttachCommand(number)}");
    return (202, Result(true, $"Starting {name}…"));
}

(int, HubActionResult) Stop(string name)
{
    IReadOnlyList<string> command;
    try { command = HubCommands.StopCommand(name); }
    catch (ArgumentException ex) { return (400, Result(false, ex.Message)); }
    if (!busy.TryAdd(name, true))
        return (409, Result(false, $"{name} is busy."));
    _ = RunSequence(name, $"Stop {name}", [command], "stopped.");
    return (202, Result(true, $"Stopping {name}…"));
}

(int, HubActionResult) StartSonar()
{
    var home = HubCommands.ResolveSonarHome(Environment.GetEnvironmentVariable("SONARQUBE_HOME"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    if (!File.Exists(Path.Combine(home, "bin", "linux-x86-64", "sonar.sh")))
        return (400, Result(false, $"No SonarQube server in {home}. Set SONARQUBE_HOME and restart aico-hub."));
    if (!busy.TryAdd(HubDefaults.SonarSandbox, true))
        return (409, Result(false, "SonarQube is already being started."));
    _ = Task.Run(async () =>
    {
        try
        {
            if (await SonarUp())
            {
                activity["sonar"] = "SonarQube is already running on http://localhost:9000.";
                return;
            }
            var exists = sandboxes.Any(s => s.Name == HubDefaults.SonarSandbox);
            var commands = HubCommands.SonarCommands(home, exists);
            foreach (var create in commands.SkipLast(1))
            {
                activity["sonar"] = "SonarQube: creating sandbox aico-sonarqube…";
                var r = await Run("sbx", create, repoDir, TimeSpan.FromMinutes(20));
                if (r.Code != 0)
                {
                    activity["sonar"] = $"SonarQube failed at 'sbx {create[0]}': {FirstLine(r)}";
                    return;
                }
            }
            StartLongRunning("sbx", commands[^1], sonarLog);
            activity["sonar"] = $"SonarQube is starting in aico-sonarqube (about a minute); http://localhost:9000. Log: {sonarLog}";
        }
        finally
        {
            busy.TryRemove(HubDefaults.SonarSandbox, out _);
            await RefreshSandboxes();
        }
    });
    return (202, Result(true, "Starting SonarQube…"));
}

(int, HubActionResult) AddTodo(string body)
{
    var t = Parse(body);
    if (string.IsNullOrWhiteSpace(t?.Text))
        return (400, Result(false, "A to-do needs text."));
    todos.Add(t.Text, DateTimeOffset.UtcNow);
    return (200, Result(true, "Added."));
}

(int, HubActionResult) UpdateTodo(string id, string body)
{
    var t = Parse(body);
    if (t?.Done is not { } done)
        return (400, Result(false, "Send {\"done\": true|false}."));
    return todos.SetDone(id, done) ? (200, Result(true, "Updated.")) : (404, Result(false, "No such to-do."));
}

TodoInput? Parse(string body)
{
    try { return JsonSerializer.Deserialize(body, json.TodoInput); }
    catch (JsonException) { return null; }
}

async Task RunSequence(string key, string label, IReadOnlyList<IReadOnlyList<string>> commands, string done)
{
    try
    {
        foreach (var cmd in commands)
        {
            activity[key] = $"{label}: sbx {cmd[0]} {cmd[1]}…";
            var r = await Run("sbx", cmd, repoDir, TimeSpan.FromMinutes(20));
            if (r.Code != 0)
            {
                activity[key] = $"{label} failed at 'sbx {cmd[0]} {cmd[1]}': {FirstLine(r)}";
                return;
            }
        }
        activity[key] = $"{label}: {done}";
    }
    finally
    {
        busy.TryRemove(key, out _);
        await RefreshSandboxes();
    }
}

// ---------------------------------------------------------------- state
HubStateResponse State()
{
    IReadOnlyList<PersonalTodo> personal;
    var all = new List<string>(notices.Values);
    try { personal = todos.Load(); }
    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
    {
        personal = [];
        all.Add($"Cannot read {todos.Path}: {ex.Message}");
    }
    all.AddRange(activity.Values);
    var waiting = tracker.Waiting()
        .Concat(pulls.Select(p => new WaitingItem("pr", $"PR #{p.Number}: {p.Title}", null, null,
            int.TryParse(p.Branch["issue-".Length..], out var n) ? n : null, p.Url, null)))
        .ToList();
    return new HubStateResponse(repo ?? "", issues, sandboxes, tracker.Snapshot(), waiting, personal, all, DateTimeOffset.UtcNow);
}

async Task RefreshSandboxes()
{
    try
    {
        var r = await Run("sbx", ["ls", "--json"], repoDir, TimeSpan.FromSeconds(20));
        if (r.Code == 0)
        {
            sandboxes = SbxList.Parse(r.Out);
            notices.TryRemove("sbx", out _);
        }
        else
        {
            notices["sbx"] = $"'sbx ls' failed: {FirstLine(r)} (is the sbx daemon running? try 'sbx login').";
        }
    }
    catch (System.ComponentModel.Win32Exception)
    {
        sandboxes = [];
        notices["sbx"] = "sbx is not installed or not on PATH. Install Docker Sandboxes (Windows: 'winget install -h Docker.sbx', " +
                         "enable Windows Hypervisor Platform, then 'sbx login') and restart aico-hub.";
    }
}

async Task RefreshGitHub()
{
    if (repo is null || string.IsNullOrWhiteSpace(token))
        return;
    try
    {
        issues = GitHubData.ParseIssues(await GitHubGet($"repos/{repo}/issues?state=open&per_page=100"));
        pulls = GitHubData.ParseIssuePulls(await GitHubGet($"repos/{repo}/pulls?state=open&per_page=100"));
        notices.TryRemove("github", out _);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        notices["github"] = $"GitHub refresh failed: {ex.Message}";
    }
}

async Task<string> GitHubGet(string url)
{
    using var msg = new HttpRequestMessage(HttpMethod.Get, url);
    msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var r = await github.SendAsync(msg);
    if (!r.IsSuccessStatusCode)
        throw new HttpRequestException($"GET {url} returned {(int)r.StatusCode}");
    return await r.Content.ReadAsStringAsync();
}

async Task<bool> SonarUp()
{
    try
    {
        return (await probe.GetAsync($"http://127.0.0.1:{HubDefaults.SonarPort}/api/system/status")).IsSuccessStatusCode;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return false;
    }
}

// ---------------------------------------------------------------- helpers
static HubActionResult Result(bool ok, string message) => new(ok, message);

static string FirstLine((int Code, string Out, string Err) r)
{
    var text = (r.Err.Trim().Length > 0 ? r.Err : r.Out).Trim();
    var line = text.Split('\n')[0].Trim();
    return line.Length == 0 ? $"exit code {r.Code}" : line.Length > 300 ? line[..300] : line;
}

static async Task<string?> ReadBody(HttpListenerRequest req, int cap)
{
    if (req.ContentLength64 > cap)
        return null;
    using var ms = new MemoryStream();
    var buffer = new byte[8192];
    int n;
    while ((n = await req.InputStream.ReadAsync(buffer)) > 0)
    {
        ms.Write(buffer, 0, n);
        if (ms.Length > cap)
            return null;
    }
    return Encoding.UTF8.GetString(ms.ToArray());
}

static Task Send(HttpListenerResponse res, int status, HubActionResult result) =>
    SendText(res, status, "application/json; charset=utf-8", JsonSerializer.Serialize(result, HubJsonContext.Default.HubActionResult));

static async Task SendText(HttpListenerResponse res, int status, string contentType, string body)
{
    var bytes = Encoding.UTF8.GetBytes(body);
    res.StatusCode = status;
    res.ContentType = contentType;
    res.Headers["Cache-Control"] = "no-store";
    res.Headers["X-Content-Type-Options"] = "nosniff";
    res.ContentLength64 = bytes.Length;
    await res.OutputStream.WriteAsync(bytes);
    res.Close();
}

/// <summary>Runs a process with ArgumentList (no shell, no string concatenation) and captures its output.</summary>
static async Task<(int Code, string Out, string Err)> Run(string file, IEnumerable<string> arguments, string? cwd, TimeSpan timeout)
{
    var psi = new ProcessStartInfo(file)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = cwd ?? "",
    };
    foreach (var a in arguments)
        psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    p.StandardInput.Close();
    var stdout = p.StandardOutput.ReadToEndAsync();
    var stderr = p.StandardError.ReadToEndAsync();
    using var cts = new CancellationTokenSource(timeout);
    try
    {
        await p.WaitForExitAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        return (-1, "", $"timed out after {timeout.TotalSeconds:0} s");
    }
    return (p.ExitCode, await stdout, await stderr);
}

/// <summary>Starts a long-running process (the SonarQube console) without waiting; its output goes to a log file.</summary>
static void StartLongRunning(string file, IEnumerable<string> arguments, string logPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
    var log = TextWriter.Synchronized(new StreamWriter(logPath, append: true) { AutoFlush = true });
    var psi = new ProcessStartInfo(file)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    foreach (var a in arguments)
        psi.ArgumentList.Add(a);
    var p = Process.Start(psi)!;
    p.StandardInput.Close();
    p.OutputDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
    p.ErrorDataReceived += (_, e) => { if (e.Data is not null) log.WriteLine(e.Data); };
    p.EnableRaisingEvents = true;
    p.Exited += (_, _) => { log.WriteLine($"[aico-hub] sbx exec exited with {p.ExitCode}"); log.Dispose(); };
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
}

static class Page
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>AICO Hub</title>
<style>
:root { --bg:#f7f7f8; --panel:#fff; --text:#1d1d1f; --muted:#6b6b73; --line:#e3e3e8; --accent:#2f5fd0; --warn:#b4231b; --ok:#1e7a3d; --chip:#eef0f5; }
@media (prefers-color-scheme: dark) { :root { --bg:#151517; --panel:#1f1f23; --text:#ececf1; --muted:#9a9aa6; --line:#33333a; --accent:#7ea2ff; --warn:#ff7b72; --ok:#56d364; --chip:#2a2a31; } }
* { box-sizing:border-box; }
body { margin:0; background:var(--bg); color:var(--text); font:14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
header { display:flex; flex-wrap:wrap; gap:12px; align-items:center; padding:12px 16px; border-bottom:1px solid var(--line); }
header h1 { font-size:16px; margin:0; }
header .repo { color:var(--muted); }
header .spacer { flex:1; }
#notices { padding:0 16px; }
.notice { margin:8px 0 0; padding:8px 10px; border:1px solid var(--line); border-left:3px solid var(--accent); background:var(--panel); border-radius:6px; }
main { display:grid; grid-template-columns:repeat(3, minmax(0, 1fr)); gap:12px; padding:12px 16px 24px; }
@media (max-width: 900px) { main { grid-template-columns:1fr; } }
section { background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:10px 12px; min-width:0; }
h2 { font-size:14px; margin:0 0 8px; }
h3 { font-size:12px; text-transform:uppercase; letter-spacing:.04em; color:var(--muted); margin:14px 0 6px; }
.card { border:1px solid var(--line); border-radius:6px; padding:8px; margin:0 0 8px; overflow-wrap:anywhere; }
.row { display:flex; flex-wrap:wrap; gap:6px; align-items:center; margin-top:6px; }
.muted { color:var(--muted); }
.chip { background:var(--chip); border-radius:10px; padding:0 8px; font-size:12px; }
.chip.crit { color:var(--warn); font-weight:600; }
.run { color:var(--ok); } .end { color:var(--muted); }
button { font:inherit; font-size:12px; padding:3px 9px; border-radius:5px; border:1px solid var(--line); background:var(--chip); color:var(--text); cursor:pointer; }
button.primary { background:var(--accent); border-color:var(--accent); color:#fff; }
button:disabled { opacity:.5; cursor:default; }
a { color:var(--accent); }
ul { margin:4px 0 0; padding-left:18px; }
li.completed { text-decoration:line-through; color:var(--muted); }
li.in_progress { font-weight:600; }
.sub { margin:6px 0 0 12px; padding-left:8px; border-left:2px solid var(--line); }
form { display:flex; gap:6px; }
input[type=text] { flex:1; min-width:0; font:inherit; padding:4px 8px; border-radius:5px; border:1px solid var(--line); background:var(--bg); color:var(--text); }
.todo { display:flex; gap:6px; align-items:center; margin:4px 0; }
.todo span { flex:1; overflow-wrap:anywhere; }
.todo.done span { text-decoration:line-through; color:var(--muted); }
#toast { position:fixed; bottom:12px; left:50%; transform:translateX(-50%); background:var(--text); color:var(--bg); padding:6px 12px; border-radius:6px; display:none; max-width:90vw; }
</style>
</head>
<body>
<header>
  <h1>AICO Hub</h1><span class="repo" id="repo"></span><span class="spacer"></span>
  <span class="muted" id="updated"></span>
  <button id="sonar" type="button">Start SonarQube</button>
</header>
<div id="notices"></div>
<main>
  <section><h2>Issues</h2><div id="issues"></div></section>
  <section><h2>Agents</h2><div id="agents"></div></section>
  <section><h2>To-do's</h2>
    <h3>Agent task lists</h3><div id="tasks"></div>
    <h3>Waiting on me</h3><div id="waiting"></div>
    <h3>Personal</h3>
    <form id="todo-form"><input type="text" id="todo-text" maxlength="500" placeholder="Add a to-do (stays on this machine)"><button class="primary" type="submit">Add</button></form>
    <div id="todos"></div>
  </section>
</main>
<div id="toast"></div>
<script nonce="{{NONCE}}">
const TOKEN = "{{TOKEN}}";
const $ = id => document.getElementById(id);
function el(tag, props, ...kids) {
  const e = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (k === 'class') e.className = v; else if (k.startsWith('on')) e.addEventListener(k.slice(2), v); else if (v !== null && v !== undefined && v !== false) e.setAttribute(k, v === true ? '' : v);
  }
  for (const k of kids.flat()) if (k !== null && k !== undefined && k !== false) e.append(k instanceof Node ? k : String(k));
  return e;
}
function toast(msg) { const t = $('toast'); t.textContent = msg; t.style.display = 'block'; clearTimeout(toast.h); toast.h = setTimeout(() => t.style.display = 'none', 4000); }
async function api(method, url, body) {
  try {
    const r = await fetch(url, { method, headers: { 'Content-Type': 'application/json', 'X-AICO-Token': TOKEN }, body: JSON.stringify(body || {}) });
    const j = await r.json().catch(() => ({ ok: r.ok, message: r.statusText }));
    if (j.message) toast(j.message);
  } catch (e) { toast('Hub not reachable: ' + e.message); }
  refresh();
}
async function copy(text) {
  try { await navigator.clipboard.writeText(text); toast('Copied: ' + text); }
  catch { window.prompt('Copy this command:', text); }
}
const safeUrl = u => /^https:\/\/github\.com\//.test(u || '') ? u : null;
const ago = t => { if (!t) return ''; const s = Math.max(0, (Date.now() - new Date(t)) / 1000); return s < 90 ? Math.round(s) + 's' : s < 5400 ? Math.round(s / 60) + 'm' : Math.round(s / 3600) + 'h'; };
const dur = s => s == null ? '' : s < 90 ? Math.round(s) + 's' : Math.round(s / 60) + 'm';
const sandboxName = n => 'aico-issue-' + n;

function render(s) {
  $('repo').textContent = s.repo || '';
  $('updated').textContent = 'updated ' + new Date(s.updated).toLocaleTimeString();
  $('notices').replaceChildren(...s.notices.map(n => el('div', { class: 'notice' }, n)));
  const boxes = new Map(s.sandboxes.map(b => [b.name, b]));

  $('issues').replaceChildren(...(s.issues.length ? s.issues.map(i => {
    const box = boxes.get(sandboxName(i.number));
    const running = box && /run/i.test(box.status);
    return el('div', { class: 'card' },
      el('div', {}, el('strong', {}, '#' + i.number + ' '), safeUrl(i.url) ? el('a', { href: i.url, target: '_blank', rel: 'noopener' }, i.title) : i.title),
      el('div', { class: 'row' }, i.labels.map(l => el('span', { class: l.toLowerCase() === 'critical' ? 'chip crit' : 'chip' }, l)),
        i.assignees.length ? el('span', { class: 'muted' }, '@' + i.assignees.join(', @')) : null),
      el('div', { class: 'row' },
        i.planAllowed
          ? el('button', { class: 'primary', type: 'button', disabled: !!box, title: 'Start an agent in plan mode in sandbox ' + sandboxName(i.number), onclick: () => api('POST', '/api/issues/' + i.number + '/plan') }, 'Plan')
          : el('span', { class: 'chip crit', title: 'critical: no autonomous agent' }, i.planBlockedReason || 'human only'),
        el('button', { type: 'button', onclick: () => copy('sbx run --name ' + sandboxName(i.number)) }, 'Attach'),
        el('button', { type: 'button', onclick: () => copy('git fetch sandbox-' + sandboxName(i.number)) }, 'Fetch branch'),
        running ? el('button', { type: 'button', onclick: () => api('POST', '/api/sandboxes/' + encodeURIComponent(sandboxName(i.number)) + '/stop') }, 'Stop') : null,
        box ? el('span', { class: 'muted' }, box.status) : null));
  }) : [el('div', { class: 'muted' }, 'No open issues (or no GITHUB_TOKEN).')]));

  const names = [...new Set([...s.sandboxes.map(b => b.name), ...s.agents.map(a => a.name)])].sort();
  $('agents').replaceChildren(...(names.length ? names.map(name => {
    const box = boxes.get(name);
    const tracked = s.agents.find(a => a.name === name);
    const running = box && /run/i.test(box.status);
    return el('div', { class: 'card' },
      el('div', {}, el('strong', {}, name), ' ', el('span', { class: running ? 'run' : 'end' }, box ? box.status : 'not in sbx ls')),
      running && /^aico-/.test(name) ? el('div', { class: 'row' }, el('button', { type: 'button', onclick: () => api('POST', '/api/sandboxes/' + encodeURIComponent(name) + '/stop') }, 'Stop')) : null,
      (tracked ? tracked.sessions : []).map(se => el('div', { class: 'sub' },
        el('div', {}, el('span', { class: se.running ? 'run' : 'end' }, se.running ? '● running' : '○ ended'), ' session ', el('code', {}, se.id.slice(0, 8)),
          se.issue ? ' · #' + se.issue : '', se.branch ? ' · ' + se.branch : '', ' · started ' + ago(se.started) + ' ago'),
        se.subAgents.map(a => el('div', { class: 'sub' },
          el('span', { class: a.running ? 'run' : 'end' }, a.running ? '● ' : '✓ '), a.type || 'sub-agent', ' ',
          el('span', { class: 'muted' }, a.running ? 'running ' + ago(a.started) : 'done in ' + dur(a.durationSeconds)))))));
  }) : [el('div', { class: 'muted' }, 'No sandboxes.')]));

  const lists = [];
  for (const b of s.agents) for (const se of b.sessions) {
    if (se.tasks.length) lists.push(taskList(b.name + (se.issue ? ' · #' + se.issue : ''), se.tasks));
    for (const a of se.subAgents) if (a.tasks.length) lists.push(taskList(b.name + ' › ' + (a.type || 'sub-agent'), a.tasks));
  }
  $('tasks').replaceChildren(...(lists.length ? lists : [el('div', { class: 'muted' }, 'No agent task lists.')]));

  $('waiting').replaceChildren(...(s.waiting.length ? s.waiting.map(w => el('div', { class: 'card' },
    w.kind === 'pr' && safeUrl(w.url) ? el('a', { href: w.url, target: '_blank', rel: 'noopener' }, w.title) : w.title,
    w.kind === 'agent' ? el('div', { class: 'row' }, el('span', { class: 'muted' }, w.sandbox + (w.issue ? ' · #' + w.issue : '') + ' · ' + ago(w.since) + ' ago'),
      w.issue ? el('button', { type: 'button', onclick: () => copy('sbx run --name ' + sandboxName(w.issue)) }, 'Attach') : null) : null))
    : [el('div', { class: 'muted' }, 'Nothing is waiting on you.')]));

  $('todos').replaceChildren(...s.todos.map(t => el('div', { class: t.done ? 'todo done' : 'todo' },
    el('input', { type: 'checkbox', checked: t.done, onchange: e => api('PUT', '/api/todos/' + encodeURIComponent(t.id), { done: e.target.checked }) }),
    el('span', {}, t.text),
    el('button', { type: 'button', title: 'Delete', onclick: () => api('DELETE', '/api/todos/' + encodeURIComponent(t.id)) }, '✕'))));
}
function taskList(title, tasks) {
  return el('div', { class: 'card' }, el('div', {}, el('strong', {}, title)),
    el('ul', {}, tasks.map(t => el('li', { class: t.status }, t.text))));
}
async function refresh() {
  try { const r = await fetch('/api/state', { cache: 'no-store' }); render(await r.json()); }
  catch (e) { $('notices').replaceChildren(el('div', { class: 'notice' }, 'Hub not reachable: is aico-hub still running?')); }
}
$('todo-form').addEventListener('submit', e => { e.preventDefault(); const t = $('todo-text'); if (t.value.trim()) { api('POST', '/api/todos', { text: t.value }); t.value = ''; } });
$('sonar').addEventListener('click', () => api('POST', '/api/sonarqube/start'));
refresh();
setInterval(refresh, 5000);
</script>
</body>
</html>
""";
}
