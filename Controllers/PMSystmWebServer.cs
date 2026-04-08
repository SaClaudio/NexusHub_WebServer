/*-------------------------------------- Descrição -----------------------------------*/
/*                                                                                    */
/* FUNÇÃO: Web Server do NexssHub. Responsável por receber requisições HTTP           */
/*         dos Clients, validar autenticação (Token/Senha), interpretar protocolos    */
/*         e encaminhar para os serviços internos.                                    */
/*                                                                                    */
/* CLASSE: PMCSystmWebServer                                                          */
/*                                                                                    */
/* MÉTODOS:                                                                           */
/*         - Login:     Cria sessão e autentica credenciais                           */
/*                      → Captura headers (Token, Password, Endpoint, Protocol)       */
/*                      → Encapsula resposta sempre em PMCSystmWebSrvResp             */
/*                      → Retorna WebSrvRetCode, WebSrvRetMessage e WebSrvRetData     */
/*                                                                                    */
/*         - GetAsync:  Processa requisições do Client                                */
/*                      → Valida headers (Token, Password, Endpoint, Protocol)        */
/*                      → Autentica Tenant e Token e credenciais do uso do web server */
/*                      → Acessa o web service apontado no Endpoint                   */
/*                      → Retorna objeto PMCSystmWebSrvResp                           */
/*                                                                                    */
/* RETORNO:                                                                           */
/*         - Objeto PMCSystmWebSrvResp contendo código de status, mensagem            */
/*           e dados do item consultado.                                              */
/*                                                                                    */
/* NOTAS:                                                                             */
/*         - Implementado no modelo RESTful, seguindo boas práticas de APIs HTTP.     */
/*         - Controller atua como orquestrador, delegando lógica ao Service.          */
/*         - Service concentra regras de negócio e validações.                        */
/*         - Repository acessa banco de dados e retorna entidades tipadas.            */
/*         - Instanciação direta (sem DI) é utilizada para manter controle explícito. */
/*         - Todas as respostas são encapsuladas em PMCSystmWebSrvResp, conforme      */
/*           manual do NexusHub WebServer.                                            */
/*                                                                                    */
/*------------------------------------------------------------------------------------*/
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

public class PMCSystmWebServerController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly PMCSystmLogCenter _logCenter;
    private readonly PMCSystmTraceCenter _trcCenter;
    private readonly PMCSystmWebSrvAuthValidator _authValidator;
    private readonly PMCSystmWebSrvProdService _prodService;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PMCSystmWebServerController(
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
                AuthFunction = "login",
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

}