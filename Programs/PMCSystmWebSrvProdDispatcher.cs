/*-------------------------------------------------------------------------------------------*/
/* Descrição                                                                                 */
/*                                                                                           */
/* FUNÇÃO: Controller do Web Server responsável por atender as requisiçoes de "products"     */
/*         do NexusHub. É acionado pelo Endpoint PMCSystmWebServerProducts                   */
/*                                                                                           */
/* CLASSE: PMCSystmWebServerProdDIspatcherController                                         */
/*                                                                                           */
/* MÉTODOS:                                                                                  */
/*         - GetProdAsync:                                                                   */
/*             → Recebe protocolo informado no request                                       */
/*             → Encaminha para o serviço de produtos                                        */
/*             → Retorna resposta encapsulada em PMCSystmWebSrvResp                          */
/*                                                                                           */
/*         - GetBySkuAsync:                                                                  */
/*             → Consulta produto pelo SKU informado                                         */
/*             → Retorna objeto PMCSystmWebSrvResp com dados do produto                      */
/*                                                                                           */
/*         - GetByBarcodeAsync:                                                              */
/*             → Consulta produto pelo código de barras informado                            */
/*             → Retorna objeto PMCSystmWebSrvResp com dados do produto                      */
/*                                                                                           */
/*         - GetAllAsync:                                                                    */
/*             → Retorna lista completa de produtos                                          */
/*             → Resposta encapsulada em PMCSystmWebSrvResp                                  */
/*                                                                                           */
/* INPUTS:                                                                                   */
/*         - protocolo: objeto contendo ação e dado para realizar a função                   */
/*             • acao: 01 (consulta por SKU)                                                 */
/*                     02 (consulta por Barcode)                                             */
/*                     03 (consulta por chave genérica)                                      */
/*                     20 (adiciona item)                                                    */
/*             • dado: SKU ou Barcode                                                        */
/*                                                                                           */
/* RETORNO:                                                                                  */
/*         - Objeto PMCSystmWebSrvResp contendo código de retorno, mensagem e dados          */
/*           do produto ou lista de produtos.                                                */
/*                                                                                           */
/* TRATAMENTO DE ERROS:                                                                      */
/*         - Todas as respostas seguem padrão RESTful e encapsuladas em PMCSystmWebSrvResp   */
/*         - Logs de auditoria e rastreabilidade são registrados via PMCSystmLogCenter       */
/*         - Erros de validação ou exceções retornam códigos mapeados em WebServerRetCodes   */
/*-------------------------------------------------------------------------------------------*/


using PriceMaker_MultTenant.Programs;
using PriceMaker_MultTenant.SystemIO;
using PriceMaker_SharedLib.Models;

namespace NexusHub_WebServer.Programs
{
    public class PMCSystmWebSrvProdDispatcher
    {
        private readonly PMCSystmCoreDI _websrvdi;
        private string ipaddr;
        // Construtor com injeção de dependência
        public PMCSystmWebSrvProdDispatcher(PMCSystmCoreDI websrvdi)
        {
            _websrvdi = websrvdi;
        }
        private string className = "PMCSystmWebSrvProdDispatcher";
        private string methodName = "GetProdAsync";
        public async Task<PMCSystmWebSrvProdResp> DispatchAsync(
                Protocolo<PMCSystmWebSrvProdRequestData> protocolo,
                string tenantName,
                string userName)


        {
            ipaddr = PMCSystmGtIP.GetIpAddress(_websrvdi.HttpContextAccessor);
            try
            {
               var prodMethods = new PMCSystmWebSrvProdMethods(_websrvdi);
                switch (protocolo.Acao)
                {
                    case PMCSystmConstants.WebsrvProdBySKU:
                        return await prodMethods.GetBySkuAsync(protocolo.Dado, tenantName, userName);

                   /* case PMCSystmConstants.WebsrvProdByBarcode:
                        return prodMethods.GetByBarcode(protocolo.Dado, tenantName, userName);

                    case PMCSystmConstants.WebsrvProdAddIterm:
                        return prodMethods.AddItem(protocolo.Dado, tenantName, userName);

                    case PMCSystmConstants.WebsrvProdDelItem:
                        return prodMethods.DeleteItem(protocolo.Dado, tenantName, userName);

                    case PMCSystmConstants.WebsrvProdUpdItem:
                        return prodMethods.UpdateItem(protocolo.Dado, tenantName, userName);*/


                    default:
                        _ = Task.Run(() => _websrvdi.LogCore.PMMWpmLgCore(2,
                               ipaddr,
                               PMCSystmConstants.OriginWebServer,
                               className,
                               methodName,
                               PMCSystmMsgC.PMMmessagecenter(21, 651).Replace("xx", protocolo.Acao) + userName,
                               _websrvdi.Configuration));

                        return new PMCSystmWebSrvProdResp
                        {
                            ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdActionInvld,
                            ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 21)
                        };
                }

            }
            catch (Exception ex)
            {
                _ = Task.Run(() => _websrvdi.LogCore.PMMWpmLgCore(1,
                        ipaddr,
                        PMCSystmConstants.OriginWebServer,
                        className,
                        methodName,
                        PMCSystmMsgC.PMMmessagecenter(21, 627).Replace("...",userName) + ex.Message,
                        _websrvdi.Configuration));

                return new PMCSystmWebSrvProdResp
                {
                    ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.InternalError,
                    ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                };
            }
        }

    }
}
