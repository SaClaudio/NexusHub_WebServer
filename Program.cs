/*------------------------------------------------------------------------------------*/
/*                                                                                    */
/* FUNÇÃO: Ponto de entrada do WebServer da plataforma NexusHub.                      */
/*         Responsável por inicializar o host ASP.NET Core voltado a serviços REST,   */
/*         carregando configurações do núcleo, aplicando decriptação de segredos,     */
/*         registrando serviços centrais e configurando o pipeline HTTP.              */
/*                                                                                    */
/* CLASSE: Program                                                                    */
/*                                                                                    */
/* MÉTODOS PRINCIPAIS:                                                                */
/*         - Main: inicializa o host e executa o WebServer.                           */
/*         - Configuração:                                                            */
/*             → Carrega appsettings.json local.                                      */
/*             → Localiza e carrega o appsettings.json do núcleo (NexusHub_Settings). */
/*             → Aplica lógica de decriptação de senhas críticas (DB, SMTP, Identity).*/
/*             → Substitui placeholders na ConnectionString.                          */
/*             → Inicializa o Serilog com base na configuração central.               */
/*         - Serviços:                                                                */
/*             → Injeta IConfiguration e PMCSystmEnDe no DI.                          */
/*             → Carrega serviços centrais do núcleo via AddNucleoServices.           */
/*             → Registra serviços específicos do WebServer (AuthValidator, ProdSrv). */
/*         - Sessões: inicializa dicionário concorrente PMCDataWebSrvSessions.        */
/*         - Pipeline HTTP: configura HTTPS, roteamento, autorização e mapeamento     */
/*           apenas de Controllers (API REST).                                        */
/*                                                                                    */
/* DETALHES DE IMPLEMENTAÇÃO:                                                         */
/*         - Fail Fast: valida senhas decriptadas; em caso de erro, registra no       */
/*           EventLog do Windows e aborta startup.                                    */
/*         - Integração com núcleo: AddNucleoServices(configRoot) carrega serviços    */
/*           compartilhados definidos no núcleo (PriceMaker_MultTenant.Programs),     */
/*           garantindo consistência entre WebServer e Workers.                       */
/*         - Logging: inicializa Serilog e registra evento de startup via             */
/*           PMCSystmLogCenter, disparado assíncronamente para não bloquear           */
/*           inicialização.                                                           */
/*         - Sessões: utiliza ConcurrentDictionary para gerenciar sessões do WebSrv   */
/*           em memória, garantindo thread safety.                                    */
/*         - Configuração de URL: redefine porta/URL de escuta com base em            */
/*           PriceMakerSettings:pmurlwebserver.                                       */
/*         - Middleware: aplica ExceptionHandler em produção, HTTPS redirection,      */
/*           roteamento e autorização.                                                */
/*                                                                                    */
/* RETORNO: Host configurado e pronto para atender requisições REST, integrado ao     */
/*          núcleo da plataforma e consistente com as configurações globais.          */
/*                                                                                    */
/* OBSERVAÇÕES:                                                                       */
/*         - O WebServer não utiliza Razor Pages nem UI; expõe apenas Controllers.    */
/*         - Toda lógica de serviços centrais é delegada ao núcleo via                */
/*           AddNucleoServices, evitando duplicação de código.                        */
/*         - O log inicial de startup é gravado para rastreabilidade, confirmando     */
/*           que o servidor iniciou corretamente.                                     */
/*         - A inicialização de sessões é crítica para controle de usuários e         */
/*           requisições simultâneas.                                                 */
/*         - A configuração explícita do Serilog garante que o TraceCenter            */
/*           utilize o logger corretamente, evitando perda silenciosa de eventos.     */
/*                                                                                    */
/*------------------------------------------------------------------------------------*/
using NexusHub_WebServer.Controllers;
using PriceMaker_MultTenant.Data;
using PriceMaker_MultTenant.Programs;
using PriceMaker_SharedLib.Models;
using Serilog;
using Serilog.Events;
using System.Collections.Concurrent;
using System.Configuration;
using System.Diagnostics;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// 🔎 Parte 1 — Carregamento do master appsettings.json
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

var builtConfig = builder.Configuration;
var nucleoPath = builtConfig["NexusHub_Settings:ConfigFile"];

if (string.IsNullOrEmpty(nucleoPath))
    throw new InvalidOperationException("NexusHub_Settings:ConfigFile não definido.");

if (!File.Exists(nucleoPath))
    throw new FileNotFoundException($"Arquivo master não encontrado: {nucleoPath}");

// Carrega diretamente o appsettings.json do núcleo
builder.Configuration.AddJsonFile(nucleoPath, optional: false, reloadOnChange: false);
var configBuilder = new ConfigurationBuilder().AddConfiguration(builder.Configuration);
IConfigurationRoot configRoot = configBuilder.Build();

// 🔑 Inicialização do Serilog no WebServer
var logsDir = configRoot["PriceMakerSettings:pmpaths:pmlogpath"];
if (string.IsNullOrWhiteSpace(logsDir))
{
    Console.Error.WriteLine("ERRO: configuração 'PriceMakerSettings:pmpaths:pmlogpath' não encontrada. Abortando.");
    Environment.Exit(1);
}
if (!Directory.Exists(logsDir))
{
    Console.Error.WriteLine($"ERRO: diretório de logs '{logsDir}' não existe. Abortando.");
    Environment.Exit(1);
}

var date = DateTime.Now.ToString("ddMMyyyy");
var pathLogWebSrv = Path.Combine(logsDir, $"{PMCSystmConstants.Logworkr}_{date}.txt");
var pathTrcWebSrv = Path.Combine(logsDir, $"{PMCSystmConstants.Traceworkr}_{date}.txt");

// helper local para extrair valor de propriedade de LogEvent
static string GetPropValue(LogEvent evt, string propName)
{
    if (evt.Properties.TryGetValue(propName, out var prop))
    {
        var s = prop.ToString();
        return s?.Trim('"');
    }
    return null;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .Enrich.FromLogContext()
     .WriteTo.Console(
        restrictedToMinimumLevel: LogEventLevel.Information,
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"
    )

    // Logs Workers e Web Server (Origin=Workers && LogType != Trace)
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(evt =>
        {
            var origin = GetPropValue(evt, "Origin");
            var logType = GetPropValue(evt, "LogType");
            return string.Equals(origin, "Worker", StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(logType, "Trace", StringComparison.OrdinalIgnoreCase);
        })
        .WriteTo.Async(a => a.File(
            pathLogWebSrv,
            shared: false,
            outputTemplate: "{Timestamp:yyyy-MM-dd}‡{Timestamp:HH:mm:ss.fff}‡{Timestamp:zzz}‡{Level:u3}‡{Message:lj}{NewLine}{Exception}"
        ))
    )

    // Traces Workers e Web Server (Origin=Worker && LogType == Trace)
    .WriteTo.Logger(lc => lc
        .Filter.ByIncludingOnly(evt =>
        {
            var origin = GetPropValue(evt, "Origin");
            var logType = GetPropValue(evt, "LogType");
            return string.Equals(origin, "Worker", StringComparison.OrdinalIgnoreCase)
                   && string.Equals(logType, "Trace", StringComparison.OrdinalIgnoreCase);
        })
        .WriteTo.Async(a => a.File(
            pathTrcWebSrv,
            shared: false,
            outputTemplate: "{Timestamp:yyyy-MM-dd}‡{Timestamp:HH:mm:ss.fff}‡{Timestamp:zzz}‡{Level:u3}‡{Message:lj}{NewLine}{Exception}"
        ))
    )

    .CreateLogger();
builder.Logging.ClearProviders(); // limpa os providers padrão
builder.Logging.AddSerilog(Log.Logger, dispose: true);
builder.Host.UseSerilog(Log.Logger);

var encryptionService = new PMCSystmEnDe(configRoot);
string source = "PriceMaker";
string log = "Application";

try
{
    encryptionService.DecryptAndStorePasswords();

    // Valida senhas críticas (Fail Fast)
    string[] keys = { "pmDBpassw", "subDBpassw", "idDBpassw", "SmtpUpsw" };
    foreach (var key in keys)
    {
        var decrypted = encryptionService.GetDecryptedPassword(key);
        if (decrypted == "#error")
        {
            EventLog.WriteEntry(source, PMCSystmMsgC.PMMmessagecenter(21, 559).Replace("xxx", key), EventLogEntryType.Error);
            throw new InvalidOperationException();
        }
    }

    // Atualiza valores de configuração
    var updatedValues = new Dictionary<string, string>
    {
        { "PriceMakerSettings:pmDBconfig:pmDBpasswdecrypt", encryptionService.GetDecryptedPassword("pmDBpassw") },
        { "PriceMakerSettings:subDBconfig:subDBpasswdecrypt", encryptionService.GetDecryptedPassword("subDBpassw") },
        { "PriceMakerSettings:IdentityConfig:idDBpasswdecrypt", encryptionService.GetDecryptedPassword("idDBpassw") },
        { "PriceMakerSettings:EmailConfigurationl:smtppasswdecrypt", encryptionService.GetDecryptedPassword("SmtpUpsw") },
        { "ConnectionStrings:PriceMaker_MultTenantContextConnection", configRoot["ConnectionStrings:PriceMaker_MultTenantContextConnection"] }
    };

    var connStr = updatedValues["ConnectionStrings:PriceMaker_MultTenantContextConnection"];
    connStr = connStr.Replace("********", updatedValues["PriceMakerSettings:IdentityConfig:idDBpasswdecrypt"]);
    updatedValues["ConnectionStrings:PriceMaker_MultTenantContextConnection"] = connStr;

    configBuilder.AddInMemoryCollection(updatedValues!);
}
catch (Exception ex)
{
    if (!EventLog.SourceExists(source))
        EventLog.CreateEventSource(source, log);

    EventLog.WriteEntry(source, $"Erro fatal durante startup.cs: {ex.Message}", EventLogEntryType.Error);
    EventLog.WriteEntry(source, $"Trace: {ex.StackTrace}", EventLogEntryType.Error);
    throw;
}

configRoot = configBuilder.Build();

// Injeta no DI
builder.Services.AddSingleton<IConfiguration>(configRoot);
builder.Services.AddSingleton(encryptionService);

// Carrega serviços centrais do núcleo
builder.Services.AddNucleoServices(configRoot);

// Controllers REST
builder.Services.AddControllers()
    .AddNewtonsoftJson(options =>
        options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore);

builder.Services.AddScoped<PMCSystmWebSrvAuthValidator>();
builder.Services.AddScoped<PMCSystmWebSrvProdService>();

// 🔑 Inicialização do dicionário de sessões
PMCDataWebSrvSessions.WebSrvSessions = new ConcurrentDictionary<string, PMCDataWebSrvSessions.Session>();

// Configuração da URL de escuta
var pmUrlWebServer = configRoot["PriceMakerSettings:pmurlwebserver"];
if (!string.IsNullOrEmpty(pmUrlWebServer))
    builder.WebHost.UseUrls(pmUrlWebServer);

var app = builder.Build();

// Log inicial de startup
PMCSystmLogCenter pmclogcore = new PMCSystmLogCenter(configRoot);
_ = Task.Run(() => pmclogcore.PMMWpmLgCore(3,
                                Environment.MachineName,
                                PMCSystmConstants.OriginWebServer,
                                "StartUp",
                                "Program.cs",
                                PMCSystmMsgC.PMMmessagecenter(59, 26).Replace("xxx", configRoot["Urls"]), configRoot));

// Pipeline HTTP
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();
app.MapControllers();

app.Run();