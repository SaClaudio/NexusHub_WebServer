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

using Newtonsoft.Json;
using Org.BouncyCastle.Asn1.Ocsp;
using PriceMaker_MultTenant.Programs;
using PriceMaker_MultTenant.SystemIO;
using PriceMaker_SharedLib;
using PriceMaker_SharedLib.Models;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using static PriceMaker_SharedLib.Models.PMCSystmConstants;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace NexusHub_WebServer.Programs
{
    public class PMCSystmWebSrvProdActions
    {
        private readonly PMCSystmCoreDI _coreDI;
        public PMCSystmWebSrvProdActions(PMCSystmCoreDI core)
        {
            _coreDI = core;
        }
        private string callingClass = "PMCSystmWebSrvProdMethods";
        private string callingMethod = "";
        static string?  formatcomiss;
        static string? formatdesc;
        static string? formatmarg;
        static string? formatcost;
        /*-------------------------------------------------------------------------------*/
        /* ExecProdActions: Executa ações de produtos para o tenant especificado         */
        /*-------------------------------------------------------------------------------*/
        public async Task<PMCSystmWebSrvProdResp> ExecProdActions(
                string ipAddr,
                string token,
                PMCSystmSubsDataRequest requestData,
                PMCSystmSubsConfigRequest configRequest,
                string tenantName,
                string userName)
        {
            string additempayload = string.Empty;
            callingMethod = "ExecProdActions";
            var formatResp = new PMCSystmWebSrvProdResp();
            string acaoToLower = requestData.Acao.ToLower();
            int codeAux = 0;
            string msgAux = string.Empty;

            if (acaoToLower == PMCSystmConstants.WebsrvProdAddItem)            // Se ação de adição de item
            {
                var (criticas, request) = Validar(requestData);

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
                additempayload = System.Text.Json.JsonSerializer.Serialize(request);
            }
                        
            // Acionamento do builder para montar o request ao tenantsIO

            var tenantRequest = PMCSystmTenantsIORequestBuilder.MapToTenantIO(requestData, configRequest);
            
            tenantRequest.TenantApp_AcqCost = formatcost.ToString(CultureInfo.InvariantCulture);
            tenantRequest.TenantApp_MarginBrute = formatmarg.ToString(CultureInfo.InvariantCulture);
            tenantRequest.TenantApp_Commission = formatcomiss.ToString(CultureInfo.InvariantCulture);
            tenantRequest.TenantApp_Discount = formatdesc.ToString(CultureInfo.InvariantCulture);
            tenantRequest.TenantApp_TenantName = tenantName;

            // Determina se deve calcular os preços finais dos produtos com base na configuração do tenant

            if (tenantRequest.TenantApp_CalcularPrecoFinal)         // Se deve calcular preço final
            {
                var buildParmCalcPreco = PMCSystmTenantsIORequestBuilder.MapToCalcPrecos(tenantRequest, configRequest);
                var calcService = new PMCSystmCalP(_coreDI.Configuration);
                buildParmCalcPreco.IpAddress = ipAddr;
                buildParmCalcPreco.UsaImpostoDefaultServico = true;
                buildParmCalcPreco.AcessaBdImpostos = true;
                codeAux = 0;
                var calcResult = await calcService.PMMcalcpriceAsync(PMCSystmConstants.OriginWebServer, buildParmCalcPreco);
                switch (calcResult.CodigoRetorno)
                {
                    case "0":
                        break;
                    case "4":
                        return new PMCSystmWebSrvProdResp
                        {
                            ProdRetCode = (int)WebServerRetCodes.ProdCalcInvld,
                            ProdMessage = calcResult.MensagemErro
                        };
                    case "9":
                        return new PMCSystmWebSrvProdResp
                        {
                            ProdRetCode = (int)WebServerRetCodes.InternalError,
                            ProdMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                        };
                    default:

                        codeAux = (int)WebServerRetCodes.ProdCalcNote;
                        msgAux = calcResult.MensagemErro;
                        break;
                }
                var requestMore = new PMCSystmSubsDataRequestMore
                {
                    TenantAppExtra_LastPurchaseDate = tenantRequest.TenantApp_LastPurchaseDate,
                    TenantAppExtra_LastPurchaseQty = tenantRequest.TenantApp_PurchaseQty,
                    TenantAppExtra_LastPurchaseValue = tenantRequest.TenantApp_LastPurchaseValue,
                    TenantAppExtra_Price1 = calcResult.PrecoFinalComDesconto,
                    TenantAppExtra_Price1Date = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    TenantAppExtra_Price2 = 0,
                    TenantAppExtra_Price3 = 0,                    
                };

                tenantRequest = PMCSystmTenantsIORequestBuilder.MapToTenantsIOPrices(tenantRequest, calcResult);
                tenantRequest = PMCSystmTenantsIORequestBuilder.MapToTenantsIOMore(tenantRequest, requestMore);
            }

            // Agora tenantRequest está pronto para ser passado ao driver de I/O

            var service = new PMCSystmTenantsIO(_coreDI);
            
            var resp = await service.PMMIOdriver(tenantRequest, callingClass, callingMethod, PMCSystmConstants.OriginWebServer);



            if (resp.ItemRetCode == 0)
            {
                formatResp = MapTenantToProdResp(resp);
            }
            else
            {
                if (codeAux != 0)      // Se houve código auxiliar de retorno do cálculo de preço final, inclui na mensagem de retorno
                {
                    formatResp.ProdRetCode = codeAux;
                    formatResp.ProdMessage = msgAux;
                }
                else
                {
                    formatResp.ProdRetCode = resp.ItemRetCode;
                    formatResp.ProdMessage = resp.ItemMessage;
                }
                    
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
        /*      • Criticas: lista padronizada [0]=ReturnCode, [1]=Mensagem,    */
        /*        [2]=Total de erros, [3..n]=erros detalhados                  */
        /*      • Request: o próprio objeto validado                           */
        /*                                                                     */
        /*  Detalhamento:                                                      */
        /*  - O campo ReturnCode indica o status da validação:                 */
        /*      0 → sucesso, sem inconsistências                               */
        /*      1 → inconsistências encontradas                                */
        /*      9 → erro inesperado                                            */
        /*  - A mensagem descreve o resultado de forma textual                 */
        /*  - O total de erros facilita a leitura e o controle de fluxo        */
        /*  - A lista de erros detalha cada crítica aplicada                   */
        /*                                                                     */
        /*---------------------------------------------------------------------*/

        public static (List<string> Criticas, PMCSystmSubsDataRequest Request)
                Validar(PMCSystmSubsDataRequest body)
        {
            var erros = new List<string>();
            int returnCode = 0;
            string message = "Validação concluída com sucesso.";

            try
            {

                // ProdKey
                if (!string.IsNullOrEmpty(body.ProdKey))
                    erros.Add("Campo 'ProdKey' deve ser nulo.");

                // Tipo
                // Valida se Tipo é numérico

                if (!int.TryParse(body.Tipo, out var tipoNumerico))
                {
                    message = "Inconsistências encontradas.";
                    returnCode = 1;
                    erros.Add("Campo 'Tipo' inválido. Deve ser numérico (1 = mercadoria, 2 = serviço).");

                    var onlyTipo = new List<string>
                    {
                        returnCode.ToString(),
                        message,
                        erros.Count.ToString()
                    };
                    onlyTipo.AddRange(erros);
                    return (onlyTipo, body);
                }

                switch (tipoNumerico)
                {
                    case PMCSystmFlags.Pm_appl_itemtipoproduto: // Mercadoria
                        ValidarMercadoria(body, erros);
                        break;

                    case PMCSystmFlags.Pm_appl_itemtiposervico: // Serviço
                        ValidarServico(body, erros);
                        break;

                    default:
                        message = "Inconsistências encontradas.";
                        returnCode = 1;
                        erros.Add("Campo 'Tipo' inválido. Valores aceitos: 1 (mercadoria) ou 2 (serviço).");

                        var onlyTipo = new List<string>
                        {
                            returnCode.ToString(),
                            message,
                            erros.Count.ToString()
                        };
                        onlyTipo.AddRange(erros);
                        return (onlyTipo, body);
                }


                // Descrição (comum a ambos)
                if (string.IsNullOrWhiteSpace(body.Descricao))
                    erros.Add("Campo 'Descricao' obrigatório.");
                else if (body.Descricao.Length > 750)
                    erros.Add("Campo 'Descricao' excede 750 caracteres.");
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
                    // Verifica características
                    bool char1 = !string.IsNullOrEmpty(body.Caracteristica1);
                    bool char2 = !string.IsNullOrEmpty(body.Caracteristica2);
                    bool char3 = !string.IsNullOrEmpty(body.Caracteristica3);
                    bool char4 = !string.IsNullOrEmpty(body.Caracteristica4);

                    if (char1 && char2 && char3 && char4)
                    {
                        // Monta SKU a partir das características
                        body.Sku = $"{body.Caracteristica1}-{body.Caracteristica2}-{body.Caracteristica3}-{body.Caracteristica4}";
                    }
                    else if (!char1 && !char2 && !char3 && !char4)
                    {
                        // Nenhuma característica informada → gera a partir da descrição
                        var partes = body.Descricao.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        var elementos = partes.Take(4).Select(p => p.Substring(0, Math.Min(5, p.Length))).ToArray();

                        body.Sku = string.Join("-", elementos);

                        // Preenche características com os elementos gerados
                        body.Caracteristica1 = elementos.ElementAtOrDefault(0);
                        body.Caracteristica2 = elementos.ElementAtOrDefault(1);
                        body.Caracteristica3 = elementos.ElementAtOrDefault(2);
                        body.Caracteristica4 = elementos.ElementAtOrDefault(3);

                        erros.Add($"Campo 'Sku' não informado. Gerado automaticamente: {body.Sku}");
                    }
                    else
                    {
                        // Caso parcial: erro
                        erros.Add("Se uma característica for informada, todas as quatro devem ser preenchidas.");
                    }
                }
                
                // Custo total, Comissão, Desconto, MargemBruta
                formatcost = ValidarValorMonetario(body.CustoAquisicao, "CustoAquisicao", erros);
                formatcomiss = ValidarValorMonetario(body.Comissao, "Comissao", erros);
                formatdesc = ValidarValorMonetario(body.Desconto, "Desconto", erros);
                formatmarg = ValidarValorMonetario(body.MargemBruta, "MargemBruta", erros);


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

            var criticas = new List<string>
                {
                    returnCode.ToString(),
                    message,
                    erros.Count.ToString()
                };
            criticas.AddRange(erros);

            return (criticas, body);
        }

        /*---------------------------------------------------------------------*/
        /*  Validações específicas para mercadoria (Tipo=1)                    */
        /*---------------------------------------------------------------------*/
        private static void ValidarMercadoria(PMCSystmSubsDataRequest body, List<string> erros)
        {
            // SKU ou Barcode obrigatórios
            bool temSku = !string.IsNullOrEmpty(body.Sku) &&
                          Regex.IsMatch(body.Sku, @"^[A-Za-z0-9]{1,5}(-[A-Za-z0-9]{1,5}){3}$");

            bool temBarcode = !string.IsNullOrEmpty(body.Barcode) &&
                              Regex.IsMatch(body.Barcode, @"^\d{1,13}$");

            if (!temSku && !temBarcode)
                erros.Add("Para Tipo=1 é obrigatório informar Sku ou Barcode.");

            // NCM
            if (!string.IsNullOrEmpty(body.CodigoNcm) && !Regex.IsMatch(body.CodigoNcm, @"^\d{1,8}$"))
                erros.Add("Campo 'NCM' deve ser numérico com até 8 dígitos.");

            // Marca
            if (!string.IsNullOrEmpty(body.Marca) && body.Marca.Length > 100)
                erros.Add("Campo 'Marca' excede 100 caracteres.");

            // Quantidade adquirida
            if (!string.IsNullOrEmpty(body.QuantidadeAdquirida) && !Regex.IsMatch(body.QuantidadeAdquirida, @"^\d{1,5}$"))
                erros.Add("Campo 'QuantidadeAdquirida' deve ser numérico entre 0 e 99999.");

            

            // Unidade
            if (!string.IsNullOrEmpty(body.Unidade) && body.Unidade.Length > 8)
                erros.Add("Campo 'Unidade' deve ter no máximo 8 caracteres.");

            
        }

        /*---------------------------------------------------------------------*/
        /*  Validações específicas para serviço (Tipo=2)                       */
        /*---------------------------------------------------------------------*/
        private static void ValidarServico(PMCSystmSubsDataRequest body, List<string> erros)
        {
            // Marca obrigatoriamente "*"
            if (body.Marca != "*")
                erros.Add("Campo 'Marca' deve ser '*' quando Tipo=2.");

            // NCM obrigatoriamente "*"
            if (body.CodigoNcm != "*")
                erros.Add("Campo 'NCM' deve ser '*' quando Tipo=2.");

            // CustoDireto → custo hora/homem
            if (string.IsNullOrWhiteSpace(body.CustoAquisicao))
                erros.Add("Campo 'CustoDireto' obrigatório quando Tipo=2.");
            else if (!Regex.IsMatch(body.CustoAquisicao, @"^\d{1,3},\d{2}$"))
                erros.Add("Campo 'CustoDireto' deve estar no formato 000,00 (máximo 999,99).");
        }
        
        /*---------------------------------------------------------------------*/
        /*  Valida valores monetários (padrão Brasil)                          */
        /*---------------------------------------------------------------------*/
        private static string? ValidarValorMonetario(string? valor, string campo, List<string> erros)
        {
            if (string.IsNullOrEmpty(valor))
            {
                erros.Add($"Campo '{campo}' obrigatório. Valor não pode ser nulo ou vazio.");
                return null;
            }

            valor = valor.Trim();

            // Regex aceita tanto "123,45" quanto "1.234,56" ou "12.345,67"
            if (!Regex.IsMatch(valor, @"^\d{1,3}(\.\d{3})*,\d{2}$") && !Regex.IsMatch(valor, @"^\d+,\d{2}$"))
            {
                erros.Add($"Campo '{campo}' inválido. Deve estar no formato ###,## ou ###.###,##.");
                return null;
            }

            // Normaliza: remove pontos de milhar, mantém vírgula
            string valorNormalizado = valor.Replace(".", "");

            // Testa conversão para decimal
            if (!decimal.TryParse(valorNormalizado.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                erros.Add($"Campo '{campo}' inválido. Não foi possível converter para número.");
                return null;
            }

            if (parsed < 0)
            {
                erros.Add($"Campo '{campo}' inválido. Valor não pode ser negativo.");
                return null;
            }

            // Retorna valor já normalizado (ex: "1234,56")
            return valorNormalizado;
        }



    }
}
