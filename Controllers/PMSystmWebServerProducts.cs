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
/*             → Retorna ProdRetCode, ProdMessage e WebSrvRetData                     */
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
using NexusHub_WebServer.Programs;
using Org.BouncyCastle.Asn1.Ocsp;
using PriceMaker_MultTenant.Programs;
using PriceMaker_SharedLib.Models;
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
        private readonly PMCSystmWebSrvProdDispatcher _prodService;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public PMCSystmWebServerProductsController(
            IConfiguration config,
            PMCSystmLogCenter logCenter,
            PMCSystmTraceCenter trcCenter,
            PMCSystmWebSrvAuthValidator authValidator,
            PMCSystmWebSrvProdDispatcher prodService,
            IHttpContextAccessor httpContextAccessor)
        {
            _config = config;
            _logCenter = logCenter;
            _trcCenter = trcCenter;
            _authValidator = authValidator;
            _prodService = prodService;
            _httpContextAccessor = httpContextAccessor;
        }
        private string className = "PMCSystmWebServerProducts";
        private string methodName = "Product";

        [HttpPost("products")]
        public async Task<IActionResult> Product([FromBody] PMCSystmWebSrvProdRequest<PMCSystmWebSrvProdRequestData> body)
        {
            string ipAddr = PMCSystmGtIP.GetIpAddress(_httpContextAccessor);
            try
            {
                var requestDto = new PMCSystmWebSrvProdRequest<PMCSystmWebSrvProdRequestData>
                {
                    Endpoint = body.Endpoint,
                    Protocolo = body.Protocolo
                };
                var lowerEndpoint = requestDto.Endpoint.ToLower();
                var requestToAuth = new PMCSystmWebSrvAuthRequest
                {
                    AuthFunction = requestDto.Endpoint,
                    AuthToken = Request.Headers["Authorization"].FirstOrDefault(),
                    AuthPassword = string.Empty, // nulo, mas mantido por consistência
                    AuthEndpoint = lowerEndpoint,
                    AuthIpaddr = ipAddr
                };

                var websrvresponse = new PMCSystmWebSrvProdResp
                {
                    ProdRetCode = (int)WebServerRetCodes.OK,
                    ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 0)
                };

                // Confirma que o endpoint é "products"
                if (lowerEndpoint != PMCSystmConstants.WebsrvEndpointProduct)
                {
                    websrvresponse.ProdRetCode = (int)WebServerRetCodes.Endpointinvldroute;
                    websrvresponse.ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 27)
                        .Replace("...", PMCSystmConstants.WebsrvEndpointProduct);
                    return BadRequest(websrvresponse);
                }

                /*--- Verifica se tem Protocol no body e se contém ação + dado ---*/
                if (requestDto.Protocolo == null)
                {
                    return BadRequest(new PMCSystmWebSrvProdResp
                    {
                        ProdRetCode = (int)WebServerRetCodes.ProtocolMissing,
                        ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 1)  
                    });
                }
                if (string.IsNullOrEmpty(requestDto.Protocolo.Acao)) 
                { 
                   return BadRequest(new PMCSystmWebSrvProdResp
                    {
                       ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdActionInvld,
                       ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 21)
                   });
                }
                if (requestDto.Protocolo.Dado == null)
                {
                    return BadRequest(new PMCSystmWebSrvProdResp
                    {
                        ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdDataInvld,
                        ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 22)
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

                var prodResponse = await _prodService.DispatchAsync(
                    requestDto.Protocolo,
                    tenantName,
                    userName
                );

                return Ok(prodResponse);

            }
            catch (Exception ex)
            {
                _ = Task.Run(() => _logCenter.PMMWpmLgCore(1,
                    ipAddr,
                    PMCSystmConstants.OriginWebServer,
                    "PMCSystmWebServer",
                    "Product",
                    PMCSystmMsgC.PMMmessagecenter(21, 627) + ex.Message,
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
