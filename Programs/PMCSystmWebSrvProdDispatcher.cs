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

                var parts = protocolo?.Split('|');

                if (parts == null)          /* Protocolo nulo. Inválido */
                {
                    return new PMCSystmWebSrvProdResp
                    {
                        ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdLayoutInvalid,
                        ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 1)
                    };
                }

                string acao = protocolo.Dado.Sku; /* Ação é o primeiro elemento do protocolo, que deve ser o SKU, Barcode ou chave genérica dependendo da ação */
                string dado;
                
                if (parts.Length == 1) /* Se só tem acao no protocolo */
                {
                    dado = null;
                }
                else
                {
                    dado = parts[1];
                }
                if (acao != "20")                           // Se é ação direta em produtos 
                {
                    if (string.IsNullOrEmpty(dado))         // Se não tem dado, então é erro. Tem que ter a chave para acessar o item  
                    {
                        return new PMCSystmWebSrvProdResp
                        {
                            ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdDataInvld,
                            ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 22)
                        };
                    }
                }
                
                switch (acao)
                {
                    case PMCSystmConstants.WebsrvProdBySKU:
                        return PMCSystmWebSrvProdMethods.GetBySKU(protocolo.dado);

                    case PMCSystmConstants.WebsrvProdByBarcode:
                        return PMCSystmWebSrvProdMethods.GetByBarcode(protocolo.dado);

                    case PMCSystmConstants.WebsrvProdAddItem:
                        return PMCSystmWebSrvProdMethods.AddItem(protocolo.dado);

                    case PMCSystmConstants.WebsrvProdDelItem:
                        return PMCSystmWebSrvProdMethods.DeleteItem(protocolo.dado);

                    case PMCSystmConstants.WebsrvProdUpdItem:
                        return PMCSystmWebSrvProdMethods.UpdateItem(protocolo.dado);


                    default:
                        _ = Task.Run(() => _websrvdi.LogCore.PMMWpmLgCore(2,
                               ipaddr,
                               PMCSystmConstants.OriginWebServer,
                               className,
                               methodName,
                               PMCSystmMsgC.PMMmessagecenter(21, 651) + UserName,
                               _websrvdi.Configuration));

                        return new PMCSystmWebSrvProdResp
                        {
                            ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdActionInvld,
                            ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 21)
                        };
                }
                
                // Assinalamento do DTO amplo (PMCSystmTenantIOResp resultObjt) para o DTO de resposta (PMCSystmWebSrvProdResp)
                var prodResult = new PMCSystmWebSrvProdResp
                {
                    ProdRetCode = resultObjt.ItemRetCode,
                    ProdMessage = resultObjt.ItemMessage,
                    ProdAction = resultObjt.ItemAction,
                    ProdType = null, // não há coluna explícita; ajuste se existir ItemType
                    ProdBarcode = resultObjt.ItemBarcode,
                    ProdSku = resultObjt.ItemSku,
                    ProdTitle = resultObjt.ItemTitle,
                    ProdBrand = resultObjt.ItemBrandNormalized, // ou ItemBrand se preferir o original
                    ProdUnit = resultObjt.ItemUnit,
                    ProdQuantity = resultObjt.ItemStockQty ?? resultObjt.ItemLastPurchaseQty,
                    ProdMargin = resultObjt.ItemMargin,
                    ProdTotalCost = resultObjt.ItemSalesTotal ?? resultObjt.ItemTotalSales,
                    ProdNcmCode = resultObjt.ItemNcmCode,
                    ProdFeature1 = resultObjt.ItemChar1,
                    ProdFeature2 = resultObjt.ItemChar2,
                    ProdFeature3 = resultObjt.ItemChar3,
                    ProdFeature4 = resultObjt.ItemChar4,
                    ProdPrice = resultObjt.ItemPriceFinal ?? resultObjt.ItemPriceBsc
                };

                return new PMCSystmWebSrvProdResp
                {
                    ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.OK
                };
            }
            catch (Exception ex)
            {
                _ = Task.Run(() => _websrvdi.LogCore.PMMWpmLgCore(1,
                        ipaddr,
                        PMCSystmConstants.OriginWebServer,
                        className,
                        methodName,
                        PMCSystmMsgC.PMMmessagecenter(21, 627) + ex.Message,
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
