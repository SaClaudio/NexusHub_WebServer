/*-------------------------------------------------------------------------------------------*/
/* Descrição                                                                                 */
/*                                                                                           */
/* FUNÇÃO: Controller de Produtos do Web Server do NexusHub.                                 */
/*         Responsável por validar credenciais, processar protocolos de produtos e           */
/*         encaminhar requisições ao serviço PMCSystmWebSrvProdService.                      */
/*                                                                                           */
/* CLASSE: PMCSystmWebServerProductsController                                               */
/*                                                                                           */
/* MÉTODOS:                                                                                  */
/*         - Products:                                                                       */
/*             → Recebe requisição POST para /nexushub-webserver/products                    */
/*             → Valida headers (Token, Password, Endpoint, Protocol, IP)                    */
/*             → Encaminha protocolo ao PMCSystmWebSrvProdService                            */
/*             → Encapsula resposta em PMCSystmWebSrvResp                                    */
/*             → Registra log da operação                                                    */
/*             → Retorna ProdRetCode, ProdMessage e WebSrvRetData                            */
/*                                                                                           */
/* RETORNO:                                                                                  */
/*         - Objeto PMCSystmWebSrvResp contendo código de status, mensagem                   */
/*           e dados do produto ou lista de produtos.                                        */
/*                                                                                           */
/* NOTAS:                                                                                    */
/*         - Implementado no modelo RESTful, seguindo boas práticas de APIs HTTP.            */
/*         - Controller atua como orquestrador, delegando lógica ao ProdService.             */
/*         - Todas as respostas são encapsuladas em PMCSystmWebSrvResp, conforme manual.     */
/*         - Logs são registrados via PMCSystmLogCenter para auditoria e rastreabilidade.    */
/*         - Sessões são validadas pelo AuthValidator e controladas pelo dicionário          */
/*           PMCDataWebSrvSessions.                                                          */
/*-------------------------------------------------------------------------------------------*/

using Microsoft.AspNetCore.Mvc;
using MySqlX.XDevAPI;
using Newtonsoft.Json;
using NexusHub_WebServer.Programs;
using Org.BouncyCastle.Asn1.Ocsp;
using PriceMaker_MultTenant.Programs;
using PriceMaker_SharedLib.Models;
using System.Text.Json;
using static PriceMaker_SharedLib.Models.PMCSystmConstants;

namespace NexusHub_WebServer.Controllers
{
    [ApiController]
    [Route("nexushub-webserver")]

    public class PMCSystmWebServerProductsController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly PMCSystmLogCenter _logCenter;
        private readonly PMCSystmTraceCenter _trcCenter;
        private readonly PMCSystmWebSrvAuthValidator _authValidator;
        private readonly PMCSystmWebSrvProdActions _prodActions;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public PMCSystmWebServerProductsController(
            IConfiguration config,
            PMCSystmLogCenter logCenter,
            PMCSystmTraceCenter trcCenter,
            PMCSystmWebSrvAuthValidator authValidator,
            PMCSystmWebSrvProdActions prodActions,
            IHttpContextAccessor httpContextAccessor)
        {
            _config = config;
            _logCenter = logCenter;
            _trcCenter = trcCenter;
            _authValidator = authValidator;
            _prodActions = prodActions;
            _httpContextAccessor = httpContextAccessor;
        }
        private string className = "PMCSystmWebServerProducts";
        private string methodName = "Product";

        [HttpPost("products")]
        public async Task<IActionResult> Product([FromBody] PMCSystmSubsDataRequest body)
        {
            string ipAddr = PMCSystmGtIP.GetIpAddress(_httpContextAccessor);
            try
            {
                
                var requestToAuth = new PMCSystmWebSrvAuthRequest
                {
                    AuthFunction = "products",
                    AuthToken = Request.Headers["Authorization"].FirstOrDefault(),
                    AuthPassword = string.Empty, // nulo, mas mantido por consistência
                    AuthIpaddr = ipAddr
                };

                var websrvresponse = new PMCSystmWebSrvProdResp
                {
                    ProdRetCode = (int)WebServerRetCodes.OK,
                    ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 0)
                };

                /*--- Faz critica cruzada da ação x dado na requisição ---*/
                if (string.IsNullOrEmpty(body.Acao))
                {
                    return BadRequest(new PMCSystmWebSrvProdResp
                    {
                        ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdActionInvld,
                        ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 21)
                    });
                }
                             
                switch (body.Acao.ToLower())
                {
                    case PMCSystmConstants.WebsrvProdBySKU:
                    case PMCSystmConstants.WebsrvProdByBarcode:
                    case PMCSystmConstants.WebsrvProdDelItem:
                    case PMCSystmConstants.WebsrvProdUpdItem:

                        if (string.IsNullOrEmpty(body.ProdKey))         // Se chave não informada
                        {
                            return BadRequest(new PMCSystmWebSrvProdResp
                            {
                                ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdDataInvld,
                                ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 22).Replace("xxx", body.Acao)
                            });
                        }

                        break;
                    case PMCSystmConstants.WebsrvProdAddItem:
                        break;
                    default:
                        return BadRequest(new PMCSystmWebSrvProdResp
                        {
                            ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdActionInvld,
                            ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 21)
                        });
                }
                               
                
                // Autenticação
                var validationResponse = await _authValidator.ValidateAsync(requestToAuth);
                websrvresponse.ProdRetCode = validationResponse.AuthCode;
                websrvresponse.ProdMessage = validationResponse.AuthMessage;

                if (validationResponse.AuthCode != (int)PMCSystmConstants.WebServerRetCodes.OK)
                    return BadRequest(websrvresponse);
                // Autenticador já devolve tenantName e userName
                var tenantName = validationResponse.AuthTenant;
                var userName = validationResponse.AuthOtherData;
                var token = validationResponse.AuthTokenOriginal;
                // Instâncias da configuração do tenant do assinante
                var configRequest = new PMCSystmSubsConfigRequest { /* ... popula campos ... */ };
                var dictresp = PMCDataWebSrvSessions.GetSession(token);
                if (dictresp.ReturnCode != "0")
                {
                    _ = _logCenter.PMMWpmLgCore(2,
                            ipAddr,
                            OriginWebServer,
                            className,
                            methodName,
                            PMCSystmMsgC.PMMmessagecenter(21, 652).Replace("...", body.Acao) + userName,
                           _config);

                    return BadRequest(new PMCSystmWebSrvProdResp
                    {
                        ProdRetCode = (int)WebServerRetCodes.InternalError,
                        ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                    });

                }
                configRequest = JsonConvert.DeserializeObject<PMCSystmSubsConfigRequest>(dictresp.SubscriberConfigValues); // Configurações do assinante, como conexões de banco, chaves de API, etc.
                var prodResponse = await _prodActions.ExecProdActions(
                    ipAddr,
                    token,
                    body,
                    configRequest,
                    tenantName,
                    userName
                );
                
                switch (prodResponse.ProdRetCode)
                {
                    case 0:
                        return Ok(prodResponse);

                    case 1:         // Não encontrado
                        return BadRequest(new PMCSystmWebSrvProdResp
                        {
                            ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdKeyNotFnd,
                            ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 37)
                        });
                    
                    case 2:         // Encontrado mas existem mais de uma linha na bd com o mesmo Id. Erro grave
                        _ = Task.Run(() => _logCenter.PMMWpmLgCore(2,
                                    ipAddr,
                                    PMCSystmConstants.OriginWebServer,
                                    "PMCSystmWebServer",
                                    "Product",
                                    PMCSystmMsgC.PMMmessagecenter(21, 654) + body.Acao + " / " + body.ProdKey,
                                    _config));
                        return BadRequest(new PMCSystmWebSrvProdResp
                        {                            
                            ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.InternalError,
                            ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                        });
                    
                    case 9:
                        return BadRequest(new PMCSystmWebSrvProdResp
                        {
                            ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.InternalError,
                            ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                        });
                }
                return Ok(prodResponse);

            }
            catch (Exception ex)
            {
                _ = Task.Run(() => _logCenter.PMMWpmLgCore(1,
                    ipAddr,
                    PMCSystmConstants.OriginWebServer,
                    "PMCSystmWebServer",
                    "Product",
                    PMCSystmMsgC.PMMmessagecenter(21, 627).Replace("...",ipAddr) + ex.Message,
                    _config));

                return StatusCode(500, new PMCSystmWebSrvProdResp
                {
                    ProdRetCode = (int)WebServerRetCodes.InternalError,
                    ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                });
            }
        }

    }
}
