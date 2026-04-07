/*-----------------------------------------------------------------------------------*/
/*                                                                                   */
/* FUNÇÃO: Classe de serviço do Web Service responsável processar o protocolo de     */
/*          produtos da aplicação PriceMaker.                                        */
/*                                                                                   */
/* CLASSE: PMCSystmWebSrvProdService                                                 */
/*                                                                                   */
/* MÉTODOS:                                                                          */
/*         - GetProdAsync: Realiza o processamento do protocolo informado no request.*/
/*         - GetBySkuAsync: Consulta produto pelo SKU informado.                     */
/*         - GetByBarcodeAsync: Consulta produto pelo código de barras informado.    */
/*         - GetAllAsync: Retorna a lista completa de produtos.                      */
/*                                                                                   */
/* INPUTS:                                                                           */
/*         - protocolo : Protocolo contendo a ação e dado para realizar a função     */
/*                       podendo ser:                                                */
/*                          acao: 01 (consulta pelo SKU)                             */
/*                                02 (consulta pelo Barcode)                         */
/*                                03 (consulta por chave generica)                   */
/*                                20 (adiciona item)                                 */
/*                          dado: SKU ou BarCode                                     */
/*                                                                                   */
/* RETORNO: IEnumerable<PMCSystmWebSrvProdResp> contendo código de retorno, mensagem */
/*          e dados do produto ou lista de produtos.                                 */
/*                                                                                   */
/* TRATAMENTO DE ERROS:                                                              */
/*                                                                                   */
/*-----------------------------------------------------------------------------------*/

using PriceMaker_MultTenant.Programs;
using PriceMaker_MultTenant.SystemIO;
using PriceMaker_SharedLib.Models;

namespace NexusHub_WebServer.Controllers
{
    public class PMCSystmWebSrvProdService
    {
        private readonly PMCSystmCoreDI _websrvdi;
        private string ipaddr;
        private List<string> resultData { get; set; } = new List<string>();
        // Construtor com injeção de dependência
        public PMCSystmWebSrvProdService(PMCSystmCoreDI websrvdi)
        {
            _websrvdi = websrvdi;
        }
        private string className = "PMCSystmWebSrvProdService";
        private string methodName = "GetProdAsync";
        public async Task<PMCSystmWebSrvProdResp> GetProdAsync(string tenantName, string protocolo)
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

                string acao = parts[0];
                string dado;
                
                if (parts.Length == 1) /* Se só tem acao no protocolo */
                {
                    dado = null;
                }
                else
                {
                    dado = parts[2];
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
                int srvAction = Convert.ToInt32(acao);
                switch (srvAction)
                {
                    case 1:                             // Consulta por SKU  
                    case 2:                             // Consulta por Barcode
                    case 20:                            // Adiçõa de item
                        break;

                    default:
                        _ = Task.Run(() => _websrvdi.LogCore.PMMWpmLgCore(2,
                       ipaddr,
                       " ",
                       className,
                       methodName,
                       PMCSystmMsgC.PMMmessagecenter(21, 626) + tenantName,
                       _websrvdi.Configuration));

                        return new PMCSystmWebSrvProdResp
                        {
                            ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdActionInvld,
                            ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 21)
                        };
                }
                string dbparm = acao + "‡" + tenantName + "‡" + dado;
                var driver = new PMCSystmTenantsIO(_websrvdi);
                var (resultData, resultObjt) = await driver.PMMIOdriver(dbparm, className, methodName);

                switch (resultData[0])
                {
                    case "0":
                        break;
                    case "1":
                        return new PMCSystmWebSrvProdResp
                        {
                            ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdKeyNotFnd,
                            ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 14)
                        };
                    case "2":
                        _ = Task.Run(() => _websrvdi.LogCore.PMMWpmLgCore(3,
                                  ipaddr,
                                  " ",
                                  className,
                                  methodName,
                                  PMCSystmMsgC.PMMmessagecenter(21, 638) + dado,
                                  _websrvdi.Configuration));
                        return new PMCSystmWebSrvProdResp
                        {
                            ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdKeyDuplicd,
                            ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 14)
                        };

                    case "9":                       
                        return new PMCSystmWebSrvProdResp
                        {
                            ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.InternalError,
                            ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                        };

                    default:
                        _ = Task.Run(() => _websrvdi.LogCore.PMMWpmLgCore(2,
                                 ipaddr,
                                 " ",
                                 className,
                                 methodName,
                                 PMCSystmMsgC.PMMmessagecenter(21, 428) + resultData[0],
                                 _websrvdi.Configuration));

                        return new PMCSystmWebSrvProdResp
                        {
                            ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.InternalError,
                            ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)

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
                        " ",
                        className,
                        methodName,
                        PMCSystmMsgC.PMMmessagecenter(21, 627) + ex.Message,
                        _websrvdi.Configuration));

                return new PMCSystmWebSrvProdResp
                {
                    ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.InternalError,
                    ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 18)
                };
            }
        }

    }
}
