/*-------------------------------------------------------------------------------------------*/
/* Descrição                                                                                 */
/*                                                                                           */
/* FUNÇÃO: Controller de Login/Logout do Web Server do NexssHub.                             */
/*         Responsável por autenticar credenciais, criar e encerrar sessões no dicionário    */
/*         interno do Web Server, além de registrar logs de auditoria.                       */
/*                                                                                           */
/* CLASSE: PMCSystmWebServerLoginController                                                  */
/*                                                                                           */
/* MÉTODOS:                                                                                  */
/*         - Login:                                                                          */
/*             → Cria sessão e autentica credenciais                                         */
/*             → Captura headers (Token, Password, Endpoint, Protocol, IP)                   */
/*             → Encapsula resposta em PMCSystmWebSrvResp                                    */
/*             → Registra log de início de sessão                                            */
/*             → Retorna WebSrvRetCode, WebSrvRetMessage                                     */
/*                                                                                           */
/*         - Logout:                                                                         */
/*             → Valida credenciais e token                                                  */
/*             → Encerra sessão no dicionário do Web Server                                  */
/*             → Recupera dados da sessão (UserId, Tenant, Status, etc.)                     */
/*             → Registra log de encerramento de sessão                                      */
/*             → Retorna WebSrvRetCode, WebSrvRetMessage                                     */
/*                                                                                           */
/* RETORNO:                                                                                  */
/*         - Objeto PMCSystmWebSrvResp contendo código de status, mensagem                   */
/*           e dados da operação (login/logout).                                             */
/*                                                                                           */
/* NOTAS:                                                                                    */
/*         - Implementado no modelo RESTful, seguindo boas práticas de APIs HTTP.            */
/*         - Controller atua como orquestrador, delegando validações ao AuthValidator.       */
/*         - Todas as respostas são encapsuladas em PMCSystmWebSrvResp, conforme manual.     */
/*         - Logs são registrados via PMCSystmLogCenter para auditoria e rastreabilidade.    */
/*         - Sessões são controladas pelo dicionário PMCDataWebSrvSessions.                  */
/*-------------------------------------------------------------------------------------------*/

using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using MySqlX.XDevAPI.Relational;
using NexusHub_WebServer.Controllers;
using Org.BouncyCastle.Asn1.Ocsp;
using PriceMaker_MultTenant.Programs;
using PriceMaker_SharedLib.Models;
using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static PriceMaker_SharedLib.Models.PMCSystmConstants;

[ApiController]
[Route("nexushub-webserver")]

public class PMCSystmWebServerLoginController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly PMCSystmLogCenter _logCenter;
    private readonly PMCSystmTraceCenter _trcCenter;
    private readonly PMCSystmWebSrvAuthValidator _authValidator;
    private readonly PMCSystmWebSrvProdService _prodService;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PMCSystmWebServerLoginController(
        IConfiguration config,
        PMCSystmLogCenter logCenter,
        PMCSystmTraceCenter trcCenter,
        PMCSystmWebSrvAuthValidator authValidator,
        PMCSystmWebSrvProdService prodService,
        IHttpContextAccessor httpContextAccessor)
    {
        _config = config;
        _logCenter = logCenter;
        _trcCenter = trcCenter;
        _authValidator = authValidator;
        _prodService = prodService;
        _httpContextAccessor = httpContextAccessor;
    }
    private string className = "PMCSystmWebServer";
    private string methodName = "Login";

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] PMCSystmWebSrvRequest body)
    {
        string ipAddr = PMCSystmGtIP.GetIpAddress(_httpContextAccessor);
        try
        {
           
            var requestDto = new PMCSystmWebSrvRequest
            {
                Password = body.Password,   // senha vem do corpo
                Endpoint = body.Endpoint,
                Protocol = body.Protocol
            };

            var requestToAuth = new PMCSystmWebSrvAuthRequest
            {
                AuthFunction = requestDto.Endpoint,
                AuthToken = Request.Headers["Authorization"].FirstOrDefault(),
                AuthPassword = requestDto.Password,
                AuthEndpoint = requestDto.Endpoint,
                AuthProtocol = requestDto.Protocol,
                AuthIpaddr = ipAddr
            };

            var websrvresponse = new PMCSystmWebSrvResp();
            if (requestDto.Endpoint != PMCSystmConstants.WebsrvEndpointLogin)           // Se não for "login", então tem erro
            {
                websrvresponse.WebSrvRetCode = (int)WebServerRetCodes.Endpointinvldroute;
                websrvresponse.WebSrvRetMessage = PMCSystmMsgC.PMMmessagecenter(59, 27)
                    .Replace("...", PMCSystmConstants.WebsrvEndpointLogin);
                return BadRequest(websrvresponse);
            }
                        
            var validationResponse = await _authValidator.ValidateAsync(requestToAuth);

            websrvresponse.WebSrvRetCode = validationResponse.AuthCode;
            websrvresponse.WebSrvRetMessage = validationResponse.AuthMessage;

            // Se falhou na autenticação
            if (validationResponse.AuthCode != (int)PMCSystmConstants.WebServerRetCodes.OK)
            {
                return BadRequest(websrvresponse);
            }

            _ = Task.Run(() => _logCenter.PMMWpmLgCore(3,
               ipAddr,
               PMCSystmConstants.OriginWebServer,
               className,
               methodName,
               PMCSystmMsgC.PMMmessagecenter(21, 632)
                        .Replace("...",validationResponse.AuthOtherData)
                        .Replace("iii", ipAddr),
               _config));
            websrvresponse.WebSrvRetMessage = PMCSystmMsgC.PMMmessagecenter(59, 28);            
            return Ok(websrvresponse);

        }
        catch (Exception ex)
        {
            _ = Task.Run(() => _logCenter.PMMWpmLgCore(1,
                ipAddr,
                PMCSystmConstants.OriginWebServer,
                className,
                methodName,
                PMCSystmMsgC.PMMmessagecenter(21, 627) + ex.Message,
                _config));

            return StatusCode(500, new PMCSystmWebSrvResp
            {
                WebSrvRetCode = (int)PMCSystmConstants.WebServerRetCodes.InternalError,
                WebSrvRetMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
            });
        }
    }
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] PMCSystmWebSrvRequest body)
    {
        string ipAddr = PMCSystmGtIP.GetIpAddress(_httpContextAccessor);
        try
        {

            var requestDto = new PMCSystmWebSrvRequest
            {
                Password = body.Password,   // senha vem do corpo
                Endpoint = body.Endpoint,
                Protocol = body.Protocol
            };

            var requestToAuth = new PMCSystmWebSrvAuthRequest
            {
                AuthFunction = requestDto.Endpoint,
                AuthToken = Request.Headers["Authorization"].FirstOrDefault(),
                AuthPassword = requestDto.Password,
                AuthEndpoint = requestDto.Endpoint,
                AuthProtocol = requestDto.Protocol,
                AuthIpaddr = ipAddr
            };

            var websrvresponse = new PMCSystmWebSrvResp();
            if (requestDto.Endpoint != PMCSystmConstants.WebsrvEndpointLogout)           // Se não for "login", então tem erro
            {
                websrvresponse.WebSrvRetCode = (int)WebServerRetCodes.Endpointinvldroute;
                websrvresponse.WebSrvRetMessage = PMCSystmMsgC.PMMmessagecenter(59, 27)
                    .Replace("...", PMCSystmConstants.WebsrvEndpointLogout);
                return BadRequest(websrvresponse);
            }

            var validationResponse = await _authValidator.ValidateAsync(requestToAuth);

            websrvresponse.WebSrvRetCode = validationResponse.AuthCode;
            websrvresponse.WebSrvRetMessage = validationResponse.AuthMessage;

            // Se falhou na autenticação
            if (validationResponse.AuthCode != (int)PMCSystmConstants.WebServerRetCodes.OK)
            {
                return BadRequest(websrvresponse);
            }
            /*---------- Encerra a sessão no dicionário de sessão do Web Server ----------*/
            var websrvSessValues = PMCDataWebSrvSessions.GetSession(validationResponse.AuthTokenOriginal);
            var websrvSessDict = PMCDataWebSrvSessions.Logout(validationResponse.AuthTokenOriginal);

            if (websrvSessDict[0] != "0")           // Se retornou erro
            {
                _ = _logCenter.PMMWpmLgCore(2,
                       ipAddr,
                       PMCSystmConstants.OriginWebServer,
                       className,
                       methodName,
                       PMCSystmMsgC.PMMmessagecenter(21, 636).Replace("...", "Logout") + websrvSessDict[0],
                       _config);
                websrvresponse.WebSrvRetCode = (int)WebServerRetCodes.InternalError;
                websrvresponse.WebSrvRetMessage = PMCSystmMsgC.PMMmessagecenter(59, 7);
                return BadRequest(websrvresponse);
            }

            _ = Task.Run(() => _logCenter.PMMWpmLgCore(3,
               ipAddr,
               PMCSystmConstants.OriginWebServer,
               className,
               methodName,
               PMCSystmMsgC.PMMmessagecenter(21, 645)
                        .Replace("...", websrvSessValues.UserName)
                        .Replace("iii", ipAddr),
               _config));
            websrvresponse.WebSrvRetMessage = PMCSystmMsgC.PMMmessagecenter(59, 33);
            return Ok(websrvresponse);

        }
        catch (Exception ex)
        {
            _ = Task.Run(() => _logCenter.PMMWpmLgCore(1,
                ipAddr,
                PMCSystmConstants.OriginWebServer,
                className,
                methodName,
                PMCSystmMsgC.PMMmessagecenter(21, 627) + ex.Message,
                _config));

            return StatusCode(500, new PMCSystmWebSrvResp
            {
                WebSrvRetCode = (int)PMCSystmConstants.WebServerRetCodes.InternalError,
                WebSrvRetMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
            });
        }
    }
}