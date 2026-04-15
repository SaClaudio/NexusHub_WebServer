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
using System.Text.Json;
using System.Text.RegularExpressions;
using static PriceMaker_SharedLib.Models.PMCSystmConstants;

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
        /* ExecProdActions: Executa ações de produtos para o tenant especificado         */
        /*-------------------------------------------------------------------------------*/
        public async Task<PMCSystmWebSrvProdResp> ExecProdActions(
                PMCSystmWebSrvProdRequest  actions,
                string tenantName,
                string userName)
        {
            string additempayload = string.Empty;
            callingMethod = "ExecProdActions";
            var formatResp = new PMCSystmWebSrvProdResp();
            string acaoToLower = actions.Acao.ToLower();

            if (acaoToLower == PMCSystmConstants.WebsrvProdAddItem)            // Se ação de adição de item
            {
                var (criticas, request) = Validar(actions);

                int code = int.Parse(criticas[0]);
                string msg = criticas[1];
                int totalErros = int.Parse(criticas[2]);

                if (code == 1)              // Se houve erro de critica dos dados para adição do item, retorna mensagem padronizada com detalhes dos erros encontrados
                {
                    return new PMCSystmWebSrvProdResp
                    {
                        ProdRetCode = (int)PMCSystmConstants.WebServerRetCodes.ProdDataInvld,
                        ProdMessage = $"{msg}: {string.Join("; ", criticas.Skip(3))}"

                    };
                }
                if (code == 9)              // Se houve erro de crash ou exceção durante a validação dos dados para adição do item, retorna mensagem padronizada com detalhes do erro encontrado
                {
                    return new PMCSystmWebSrvProdResp
                    {
                        ProdRetCode = (int)WebServerRetCodes.InternalError,
                        ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                    };
                }
                additempayload = JsonSerializer.Serialize(request);
            }
            
            var service = new PMCSystmTenantsIO(_coreDI);
            string dbparm = actions.Acao + "‡" + tenantName + "‡" + actions.ProdKey + "‡" + additempayload;

            var resp = await service.PMMIOdriver(dbparm, callingClass, callingMethod, PMCSystmConstants.OriginWebServer);

              
            
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
        /*---------------------------------------------------------------------*/
        /*  Método: Validar                                                    */
        /*                                                                     */
        /*  Objetivo:                                                          */
        /*  - Receber um objeto PMCSystmWebSrvProdRequest (body)               */
        /*  - Aplicar todas as críticas e regras de negócio definidas          */
        /*    para cada campo do DTO                                           */
        /*  - Retornar uma tupla contendo:                                     */
        /*      • Criticas: lista padronizada [0]=retcode, [1]=mensagem,       */
        /*        [2]=total de erros, [3..n]=erros detectados                  */
        /*      • Request: o próprio objeto validado                           */
        /*---------------------------------------------------------------------*/
        public static (List<string> Criticas, PMCSystmWebSrvProdRequest Request)
            Validar(PMCSystmWebSrvProdRequest body)
        {
            var erros = new List<string>();
            int returnCode = 0;
            string message = "Validação concluída com sucesso.";

            try
            {
                // Acao
                if (string.IsNullOrWhiteSpace(body.Acao))
                    erros.Add("Campo 'Acao' não informado.");

                // ProdKey
                if (!string.IsNullOrEmpty(body.ProdKey))
                    erros.Add("Campo 'ProdKey' deve ser nulo.");

                // Tipo
                if (string.IsNullOrWhiteSpace(body.Tipo) || (body.Tipo != "1" && body.Tipo != "2"))
                    erros.Add("Campo 'Tipo' obrigatório. Valores aceitos: 1 (mercadoria) ou 2 (serviço).");

                // Tipo = 1 → precisa ter Sku OU Barcode
                if (body.Tipo == "1")
                {
                    bool temSku = !string.IsNullOrEmpty(body.Sku) &&
                                  Regex.IsMatch(body.Sku, @"^[A-Za-z0-9]{1,5}(-[A-Za-z0-9]{1,5}){3}$");

                    bool temBarcode = !string.IsNullOrEmpty(body.Barcode) &&
                                      Regex.IsMatch(body.Barcode, @"^\d{1,13}$");

                    if (!temSku && !temBarcode)
                        erros.Add("Para Tipo=1 é obrigatório informar Sku ou Barcode.");
                }

                // NCM
                if (!string.IsNullOrEmpty(body.CodigoNcm) && !Regex.IsMatch(body.CodigoNcm, @"^\d{1,8}$"))
                    erros.Add("Campo 'NCM' deve ser numérico com até 8 dígitos.");

                // Comissão, Desconto, MargemBruta
                ValidarValorMonetario(body.Comissao, "Comissao", erros);
                ValidarValorMonetario(body.Desconto, "Desconto", erros);
                ValidarValorMonetario(body.MargemBruta, "MargemBruta", erros);

                // Descrição
                if (string.IsNullOrWhiteSpace(body.Descricao))
                    erros.Add("Campo 'Descricao' obrigatório.");
                else if (body.Descricao.Length > 750)
                    erros.Add("Campo 'Descricao' excede 750 caracteres.");

                // Marca
                if (body.Tipo == "2" && body.Marca != "*")
                    erros.Add("Campo 'Marca' deve ser '*' quando Tipo=2.");
                else if (!string.IsNullOrEmpty(body.Marca) && body.Marca.Length > 100)
                    erros.Add("Campo 'Marca' excede 100 caracteres.");

                // Quantidade adquirida
                if (!string.IsNullOrEmpty(body.QuantidadeAdquirida) && !Regex.IsMatch(body.QuantidadeAdquirida, @"^\d{1,5}$"))
                    erros.Add("Campo 'QuantidadeAdquirida' deve ser numérico entre 0 e 99999.");

                // SKU
                if (!string.IsNullOrEmpty(body.Sku))
                {
                    if (!Regex.IsMatch(body.Sku, @"^[A-Za-z0-9]{1,5}(-[A-Za-z0-9]{1,5}){3}$"))
                        erros.Add("Campo 'Sku' deve seguir o formato XXXX-XXXX-XXXX-XXXX.");
                    else if (body.Sku.Length > 23)
                        erros.Add("Campo 'Sku' excede 23 caracteres.");
                }
                else
                {
                    if (!string.IsNullOrEmpty(body.Descricao))
                    {
                        var partes = body.Descricao.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var skuGerado = string.Join("-", partes.Take(4).Select(p => p.Substring(0, Math.Min(5, p.Length))));
                        erros.Add($"Campo 'Sku' não informado. Gerado automaticamente: {skuGerado}");
                    }
                }

                // Unidade
                if (!string.IsNullOrEmpty(body.Unidade) && body.Unidade.Length > 8)
                    erros.Add("Campo 'Unidade' deve ter no máximo 8 caracteres.");

                // ValorTotalAquisicao
                if (!string.IsNullOrEmpty(body.ValorTotalAquisicao))
                {
                    if (!Regex.IsMatch(body.ValorTotalAquisicao, @"^\d{1,9},\d{2}$"))
                        erros.Add("Campo 'ValorTotalAquisicao' deve estar no formato 999999999,99.");
                }

                // Tipo=2 → NCM deve ser "*"
                if (body.Tipo == "2" && body.CodigoNcm != "*")
                    erros.Add("Campo 'NCM' deve ser '*' quando Tipo=2.");

                // Resultado final
                if (erros.Count > 0)
                {
                    returnCode = 1;
                    message = "Inconsistências encontradas.";
                }
            }
            catch (Exception ex)
            {
                returnCode = 9;
                message = $"Erro inesperado: {ex.Message}";
            }

            // Monta lista padronizada
            var criticas = new List<string>
                {
                    returnCode.ToString(),   // [0] ReturnCode
                    message,                 // [1] Mensagem
                    erros.Count.ToString()   // [2] Total de erros
                };
            criticas.AddRange(erros);    // [3..n] Erros

            return (criticas, body);
        }

        /*---------------------------------------------------------------------*/
        /*                  Valida valores monetários                          */
        /*---------------------------------------------------------------------*/
        private static void ValidarValorMonetario(string? valor, string campo, List<string> erros)
        {
            if (!string.IsNullOrEmpty(valor) && !Regex.IsMatch(valor, @"^\d{1,3},\d{2}$"))
                erros.Add($"Campo '{campo}' deve estar no formato 000,00 (máximo 999,99).");
        }

    }
}
