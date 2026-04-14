/*-------------------------------------------------------------------------------------------*/
/* Descrição                                                                                 */
/*                                                                                           */
/* FUNÇÃO: Classe de métodos de negócio responsável por executar as ações relacionadas       */
/*         ao domínio de "products" no NexusHub. É acionada pela classe auxiliar             */
/*         PMCSystmWebSrvProdDispatcher, que interpreta o protocolo recebido pelo endpoint   */
/*         PMCSystmWebServerProducts e encaminha para o método adequado.                     */
/*                                                                                           */
/* CLASSE: PMCSystmWebSrvProdMethods                                                         */
/*                                                                                           */
/* MÉTODOS:                                                                                  */
/*         - GetBySku:                                                                       */
/*             → Consulta produto pelo SKU informado                                         */
/*             → Retorna objeto Produto ou DTO correspondente                                */
/*                                                                                           */
/*         - GetByBarcode:                                                                   */
/*             → Consulta produto pelo código de barras informado                            */
/*             → Retorna objeto Produto ou DTO correspondente                                */
/*                                                                                           */
/*         - GetAll:                                                                         */
/*             → Retorna lista completa de produtos                                          */
/*             → Utilizado para consultas gerais                                             */
/*                                                                                           */
/*         - AddItem:                                                                        */
/*             → Adiciona novo produto ao catálogo                                           */
/*             → Retorna confirmação ou objeto atualizado                                    */
/*                                                                                           */
/*         - UpdateItem:                                                                     */
/*             → Atualiza dados de produto existente                                         */
/*             → Retorna confirmação ou objeto atualizado                                    */
/*                                                                                           */
/*         - DeleteItem:                                                                     */
/*             → Remove produto do catálogo                                                  */
/*             → Retorna confirmação da exclusão                                             */
/*                                                                                           */
/* INPUTS:                                                                                   */
/*         - Objetos tipados recebidos via Dispatcher (ProdutoSkuRequest, ProdutoAddRequest, */
/*           ProdutoUpdRequest, etc.)                                                        */
/*                                                                                           */
/* RETORNO:                                                                                  */
/*         - Objetos de domínio Produto ou coleções                                          */
/*         - Resultado encapsulado pelo Dispatcher em PMCSystmWebSrvProdResp                 */
/*         - O endpoint Products é quem retorna ao client                                    */
/*                                                                                           */
/* TRATAMENTO DE ERROS:                                                                      */
/*         - Validações de entrada realizadas antes da execução                              */
/*         - Exceções capturadas e convertidas em códigos de retorno pelo Dispatcher         */
/*         - Logs de auditoria e rastreabilidade registrados via PMCSystmLogCenter           */
/*-------------------------------------------------------------------------------------------*/

using PriceMaker_MultTenant.Programs;
using PriceMaker_MultTenant.SystemIO;
using PriceMaker_SharedLib.Models;

namespace NexusHub_WebServer.Programs
{
    public class PMCSystmWebSrvProdActions
    {
        private  readonly PMCSystmCoreDI _coreDI;
        public PMCSystmWebSrvProdActions(PMCSystmCoreDI core)
        {
            _coreDI = core;
        }
        private string callingClass = "PMCSystmWebSrvProdMethods";
        private string callingMethod = "";
        /*-------------------------------------------------------------------------------*/
        /* GetBySku: Consulta Tenant do PriceMaker de aplicação do assinante pelo SKU    */
        /*-------------------------------------------------------------------------------*/
        public async Task<PMCSystmWebSrvProdResp> ExecProdActions(
                PMCSystmWebSrvProdRequest  actions,
                string tenantName,
                string userName)
        {
            callingMethod = "ExecProdActions";
            
            var service = new PMCSystmTenantsIO(_coreDI);
            string dbparm = actions.Acao + "‡" + tenantName + "‡" + actions.ProdKey;

            var resp = await service.PMMIOdriver(dbparm, callingClass, callingMethod, PMCSystmConstants.OriginWebServer);

            var formatResp = new PMCSystmWebSrvProdResp(); ;
            
            if (resp.ItemRetCode == 0)
            {
                formatResp = MapTenantToProdResp(resp);
            }
            else
            {
                formatResp.ProdRetCode = resp.ItemRetCode;
                formatResp.ProdMessage = resp.ItemMessage;
            }
                   
            return (formatResp);
        }
        /*---------------------------------------------------------------------*/
        /*                      Monta resposta do metodo                       */
        /*---------------------------------------------------------------------*/
        public static PMCSystmWebSrvProdResp MapTenantToProdResp(PMCSystmTenantsIOResp tenantResp)
        {
            return new PMCSystmWebSrvProdResp
            {
                ProdRetCode = tenantResp.ItemRetCode,
                ProdMessage = tenantResp.ItemMessage,
                ProdAction = tenantResp.ItemAction,
                ProdType = PMCSystmConstants.Prodtype[Convert.ToInt32(tenantResp.ItemTypeProd)].Text,
                ProdBarcode = tenantResp.ItemBarcode,
                ProdSku = tenantResp.ItemSku,
                ProdTitle = tenantResp.ItemTitle,
                ProdBrandnormalized = tenantResp.ItemBrandNormalized,
                ProdUnit = tenantResp.ItemUnit,
                ProdQuantitypurchased = tenantResp.ItemLastPurchaseQty,
                ProdQuantitystock = tenantResp.ItemStockQty,
                ProdMargin = tenantResp.ItemMargin,
                ProdTotalCostAcq = tenantResp.ItemLastPurchaseValue,
                ProdNcmCode = tenantResp.ItemNcmCode,
                ProdFeature1 = tenantResp.ItemChar1,
                ProdFeature2 = tenantResp.ItemChar2,
                ProdFeature3 = tenantResp.ItemChar3,
                ProdFeature4 = tenantResp.ItemChar4,
                ProdPrice = tenantResp.ItemPriceFinal
            };
        }
    }
}
