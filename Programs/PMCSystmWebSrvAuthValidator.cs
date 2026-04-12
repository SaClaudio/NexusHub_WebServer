/*-------------------------------------------------------------------------------------------*/
/* Descrição                                                                                 */
/*                                                                                           */
/* FUNÇÃO: Classe responsável por validar autenticação das requisições recebidas pelo        */
/*         Web Server do NexusHub. Centraliza a lógica de verificação dos headers            */
/*         obrigatórios (Token, Password, Endpoint, Protocolo) e confronta credenciais       */
/*         com os dados do Tenant e do Identity.                                             */
/*                                                                                           */
/* CLASSE: PMCSystmWebSrvAuthValidator                                                       */
/*                                                                                           */
/* MÉTODOS:                                                                                  */
/*         - ValidateAsync:                                                                  */
/*             → Verifica presença e formato dos headers obrigatórios                        */
/*             → Normaliza token (Bearer) e valida contra BD/Identity                        */
/*             → Confronta senha informada com composição esperada                           */
/*             → Valida Endpoint e Protocolo                                                 */
/*             → Checa se token já possui sessão ativa ou se login já foi feito              */
/*             → Cria/atualiza entrada no dicionário de sessões (PMCDataWebSrvSessions)      */
/*             → Retorna objeto PMCSystmWebSrvAuthResp com resultado da validação            */
/*                                                                                           */
/*         - CheckToken:                                                                     */
/*             → Decriptografa token recebido                                                */
/*             → Valida assinatura, expiração e consistência                                 */
/*             → Retorna objeto PMCSystmWebSrvAuthResp com status e token original           */
/*                                                                                           */
/*         - CheckTokenPassword:                                                             */
/*             → Valida senha composta (tenantName + userName + dataHora)                    */
/*             → Retorna objeto PasswordValidationResp com sucesso ou mensagem de erro       */
/*                                                                                           */
/* RETORNO:                                                                                  */
/*         - Objeto PMCSystmWebSrvAuthResp contendo código de status, mensagem e dados       */
/*           adicionais (usuário, tenant, token).                                            */
/*                                                                                           */
/* NOTAS:                                                                                    */
/*         - Toda a lógica de autenticação fica centralizada nesta classe.                   */
/*         - Controller apenas monta o DTO PMCSystmWebSrvRequest e delega a validação.       */
/*         - Sessões são controladas pelo dicionário PMCDataWebSrvSessions.                  */
/*         - Logs e traces são registrados via PMCSystmLogCore e PMCSystmTrcCore.            */
/*         - Retornos seguem códigos mapeados em PMCSystmConstants -> WebServerRetCodes.     */
/*-------------------------------------------------------------------------------------------*/

namespace NexusHub_WebServer.Programs
{
    using Microsoft.EntityFrameworkCore.Storage.ValueConversion.Internal;
    using Newtonsoft.Json.Linq;
    using Org.BouncyCastle.Asn1.Ocsp;
    using Org.BouncyCastle.Asn1.Pkcs;
    using Org.BouncyCastle.Ocsp;
    using PriceMaker_MultTenant.Programs;
    using PriceMaker_MultTenant.SystemIO;
    using PriceMaker_MultTenant.Utilities;
    using PriceMaker_SharedLib.Models;
    using PriceMaker_SharedLib.Utils;
    using System;
    using System.Collections.Generic;
    using System.IdentityModel.Tokens.Jwt;
    using System.Linq;
    using System.Reflection;
    using System.Text.Json;
    using System.Threading.Tasks;
    using static PriceMaker_SharedLib.Models.PMCSystmConstants;
    using static System.Formats.Asn1.AsnWriter;

    public class PMCSystmWebSrvAuthValidator
    {
        private readonly PMCSystmCoreDI _coreDI;
        public PMCSystmWebSrvAuthValidator(PMCSystmCoreDI core)
        {
            _coreDI = core;
        }
        public class PasswordValidationResp
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; } = string.Empty;
            public string TenantFromPsw { get; set; } = string.Empty;
        }
        private string tenantName;        /* Prefixo do nome do tenant do assinante */
        private string DecryptedRequestPassword;      // Senha decriptografada do Request
        private string EncryptedRequestPasswordDb;    // Senha criptografada do request armazenada no tenant
        private string DecryptedRequestPasswordDb;    // Senha descriptografada do request armazenada no tenantprivate string dbParm;
        private string dbParm;
        private string ipaddr;
        private string decryptToken;
        private string className = "PMCSystmWebSrvAuthValidator";
        private List<string> resultData { get; set; } = new List<string>();
        private List<string> gtknresult = new List<string>();
        



        public async Task<PMCSystmWebSrvAuthResp> ValidateAsync(PMCSystmWebSrvAuthRequest request)
        {
            ipaddr = request.AuthIpaddr ;
            int auxAuthCode = (int)WebServerRetCodes.LoggedinOk;       // Assume que login vai dar ok
            string methodName = "ValidateAsync";
            
            try
            {
                /*---------------------------------------------------------------------*/
                /* Grava entrada inicial no Trace                                      */
                /*---------------------------------------------------------------------*/
                var traceMsg = new PMCSystmTrcMsg
                {
                    Origem = OriginWebServer,
                    IpAddr = Environment.MachineName,     // Não usa IP e sim, nome do servidor, já que é um processo interno
                    MainObj = className,                  // Nome desta Classe
                    SecObj = methodName,                  // Nome do Metodo
                    Emoji = "✅",                        //  Emoji Padrão
                    MsgTxt = PMCSystmMsgC.PMMmessagecenter(0, 53),
                    MsgData = request.AuthFunction + " # " +
                    request.AuthToken + " # " +
                    request.AuthPassword + " # " +
                    request.AuthEndpoint + " # " +
                    request.AuthIpaddr
                };
                /*---------------------------------------------------------------------*/
                /* Grava entrada inicial no Trace (se estiver ativo)                   */
                /*---------------------------------------------------------------------*/
                _ = _coreDI.TrcCore.PMMWTrGlobal(traceMsg);

                var driverIO = new PMCSystmTenantsIO(_coreDI);
                /*--- Verifica se tem Function no Header ---*/
                if (string.IsNullOrEmpty(request.AuthFunction))
                {
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = (int)WebServerRetCodes.LoginRequired,
                        AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 20)
                    };
                }
                string functionlower = request.AuthFunction.ToLower();

                /*--- Verifica se tem Token no Header ---*/
                if (string.IsNullOrEmpty(request.AuthToken))
                {
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = (int)WebServerRetCodes.TokenMissing,
                        AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 2)
                    };
                }
                /*--- Verifica se o Token está no formato Bearer ---*/
                if (!request.AuthToken.StartsWith("Bearer "))
                {
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = (int)WebServerRetCodes.TokenInvalidFormat,
                        AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 29) // mensagem específica para formato inválido
                    };
                }

                // Se chegou até aqui, remove o prefixo "Bearer " e normaliza
                request.AuthToken = request.AuthToken.Substring("Bearer ".Length).Trim();
                
                /*--- Verifica se tem Endpoint no Header ---*/
                if (string.IsNullOrEmpty(request.AuthEndpoint))
                {
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = (int)WebServerRetCodes.EndpointMissing,
                        AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 3)
                    };
                }
               
                /*--------- Faz avaliação cruzada quanto ao uso da senha por endpoint. Só aceita se for login */

                if (functionlower != WebsrvEndpointLogin && !string.IsNullOrEmpty(request.AuthPassword))
                {
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = (int)WebServerRetCodes.CrossHeaderRejtPsw,
                        AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 30)
                    };

                }

                /*---------------------------- Faz validação do Token -------------------------*/

                var tokenresultCheck = new PMCSystmWebSrvAuthResp();
                tokenresultCheck = await CheckToken(request.AuthToken);
                if (tokenresultCheck.AuthCode != (int)WebServerRetCodes.OK &&
                    tokenresultCheck.AuthCode != (int)WebServerRetCodes.TokenWillExpire)
                {
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = tokenresultCheck.AuthCode,
                        AuthMessage = tokenresultCheck.AuthMessage
                    };
                }
                var gethandler = new JwtSecurityTokenHandler();
                var jwtToken = gethandler.ReadJwtToken(tokenresultCheck.AuthTokenOriginal);
                var userId = jwtToken.Claims.FirstOrDefault(c => c.Type == "keyid")?.Value;
                auxAuthCode = tokenresultCheck.AuthCode;     // Pode ser OK ou TokenWillExpire, mas é o que tem para esse cenário

                if (functionlower != WebsrvEndpointLogin)       /* Se não for função de login, valida se token tem sessão ativa no dicionário de sessões do web server */
                {
                    if (!PMCDataWebSrvSessions.TokenExists(tokenresultCheck.AuthTokenOriginal))
                    {
                        return new PMCSystmWebSrvAuthResp
                        {
                            AuthCode = (int)WebServerRetCodes.LoginRequired,
                            AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 31)
                        };
                    }

                }

                if (functionlower == WebsrvEndpointLogin)       /* Se for função de login, valida se já foi feito login antes */
                {
                    if (PMCDataWebSrvSessions.TokenExists(tokenresultCheck.AuthTokenOriginal))
                    {
                        return new PMCSystmWebSrvAuthResp
                        {
                            AuthCode = (int)WebServerRetCodes.LoginActive,
                            AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 32)
                        };
                    }

                }
                /*----------- Encaminha atendimento da função passada em Function -------------*/

                switch (functionlower)
                {
                    case "login":               // Função "Login"

                        /*---------- Acessa assinante em Identity pelo keyid que vem no header do token -----------*/


                        var _identityIO = new PMCSystmIdentityIO(_coreDI);
                        dbParm = "1" + "‡" + userId;

                        // Chama o driver de Identity
                        var (identityuser, identityrc) = await _identityIO.PMMIOdriver(dbParm,
                            className,
                            methodName,
                            OriginWebServer);
                        string userName = string.Empty;

                        switch (identityrc.ReturnCode)
                        {
                            case "0":     /* Ok, usuário encontrado e ativo */
                                userName = identityrc.identityUsr_UserName;         // UserName (normalmente email)
                                break;
                            case "1":     /* Usuário não encontrado */
                                _ = _coreDI.LogCore.PMMWpmLgCore(3,
                                    ipaddr,
                                    OriginWebServer,
                                    className,
                                    methodName,
                                    PMCSystmMsgC.PMMmessagecenter(21, 631) + userId,
                                   _coreDI.Configuration);
                                return new PMCSystmWebSrvAuthResp
                                {
                                    AuthCode = (int)WebServerRetCodes.SubscriberGone,
                                    AuthMessage = "Assinante não está mais na plataforma (removido/cancelado)."
                                };

                            default:    /* Erro no acesso ao Identity */
                                return new PMCSystmWebSrvAuthResp
                                {
                                    AuthCode = (int)WebServerRetCodes.InternalError,
                                    AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                                };
                        }

                        
                        /*--------------------------------- Valida senha informada no request --------------------------------*/
                        /* Composição da senha: tamanho do tenantName + tenantName (completo) + dataHora que foi criada no UI */
                        /*----------------------------------------------------------------------------------------------------*/
                        
                        var resp = CheckTokenPassword(request.AuthPassword, identityrc.identityUsr_TenantName, userName);

                        if (!resp.Success)
                        {
                            return new PMCSystmWebSrvAuthResp
                            {
                                AuthCode = (int)WebServerRetCodes.PasswordMismatch,
                                AuthMessage = resp.ErrorMessage
                            };
                        }
                        tenantName = resp.TenantFromPsw;     // Se senha está ok, pega tenantName extraído da senha para usar adiante
                        /*--------- Verifica se token informado no request é o mesmo que está na BD ---*/

                        auxAuthCode = tokenresultCheck.AuthCode;     // Pode ser OK ou TokenWillExpire, mas é o que tem para esse cenário

                        if (tokenresultCheck.AuthTokenOriginal != identityrc.identityUsr_TokenWebServer)        /* Se token não é igual ao gerado anteriormente */
                        {
                            return new PMCSystmWebSrvAuthResp
                            {
                                AuthCode = (int)WebServerRetCodes.TokenInvalidMismatch,
                                AuthMessage = gtknresult[2]
                            };
                        }


                        /*---------- Valida se perfil do assinante está habilitado para usar web server -----------*/

                        int subtype = Convert.ToInt32(identityrc.identityUsr_Type);
                        switch (subtype)      /* Analise tipo de assinante */
                        {
                            case PMCSystmFlags.Pm_subtype_subscriber_trialadmin:     /* Tipo de assinante válido para sessão web server */
                            case PMCSystmFlags.Pm_subtype_subscriber_definitiveadmin:
                            case PMCSystmFlags.Pm_subtype_subscriber_support:
                            case PMCSystmFlags.Pm_subtype_subscriber_pmadmin:
                                break;
                            default:
                                _ = _coreDI.LogCore.PMMWpmLgCore(3,
                                   ipaddr,
                                   OriginWebServer,
                                   className,
                                   methodName,
                                   PMCSystmMsgC.PMMmessagecenter(21, 633) + userName,
                                  _coreDI.Configuration);
                                return new PMCSystmWebSrvAuthResp
                                {
                                    AuthCode = (int)WebServerRetCodes.NotAuthSubType,
                                    AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 12)
                                };
                        }

                        int subacctype = Convert.ToInt32(identityrc.identityUsr_AcctType);
                        switch (subacctype)      /* Analise situação quanto ao acesso (acctype do identity) */
                        {
                            case PMCSystmFlags.Pm_subscriber_acctype_trial:     /* Acesso ok */
                            case PMCSystmFlags.Pm_subscriber_acctype_definitive:
                                break;

                            case PMCSystmFlags.Pm_subscriber_acctype_suspendedtrialend:
                                _ = _coreDI.LogCore.PMMWpmLgCore(3,
                                   ipaddr,
                                   OriginWebServer,
                                   className,
                                   methodName,
                                   PMCSystmMsgC.PMMmessagecenter(21, 634) + userName,
                                  _coreDI.Configuration);
                                return new PMCSystmWebSrvAuthResp
                                {
                                    AuthCode = (int)WebServerRetCodes.TrialExpired,
                                    AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 8)
                                };

                            case PMCSystmFlags.Pm_subscriber_acctype_suspendedpayment:
                                _ = _coreDI.LogCore.PMMWpmLgCore(3,
                                        ipaddr,
                                       OriginWebServer,
                                       className,
                                       methodName,
                                       PMCSystmMsgC.PMMmessagecenter(21, 635) + userId,
                                      _coreDI.Configuration);
                                return new PMCSystmWebSrvAuthResp
                                {
                                    AuthCode = (int)WebServerRetCodes.Blockedcard,
                                    AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 8)
                                };
                            default:
                                _ = _coreDI.LogCore.PMMWpmLgCore(2,
                                   ipaddr,
                                   OriginWebServer,
                                   className,
                                   methodName,
                                   PMCSystmMsgC.PMMmessagecenter(21, 634) + userName,
                                  _coreDI.Configuration);
                                return new PMCSystmWebSrvAuthResp
                                {
                                    AuthCode = (int)WebServerRetCodes.BlockedIssue,
                                    AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 8)
                                };
                        }
                        int subscriptiontype = Convert.ToInt32(identityrc.identityUsr_FlgSubType);
                        switch (subscriptiontype)      /* Analise tipo de assinatura (somente Premium ou Trial) */
                        {
                            case PMCSystmFlags.Pm_subscriber_type_premium:      /* Assinatura Premium - Acesso ok */
                            case PMCSystmFlags.Pm_subscriber_type_trial:        /* Trial - Acesso ok */ 
                                break;

                            default:
                                _ = _coreDI.LogCore.PMMWpmLgCore(2,
                                   ipaddr,
                                   OriginWebServer,
                                   className,
                                   methodName,
                                   PMCSystmMsgC.PMMmessagecenter(21, 633) + userName,
                                  _coreDI.Configuration);
                                return new PMCSystmWebSrvAuthResp
                                {
                                    AuthCode = (int)WebServerRetCodes.NotPremium,
                                    AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 23)
                                };
                        }
                        /*---------- Verifica se já existe uma sessão web server prévia ----------*/
                        
                        string ipaddrDict = PMCDataWebSrvSessions.GetIpByToken(tokenresultCheck.AuthTokenOriginal);

                        if (!string.IsNullOrEmpty(ipaddrDict))          // Se já existe um token registrado, verifica se IP é o mesmo. Se for o mesmo, mantém sessão. Se for diferente, bloqueia acesso (possível roubo de token)
                        {
                            if (!PMCDataWebSrvSessions.ValidateTokenAndIp(tokenresultCheck.AuthTokenOriginal, request.AuthIpaddr))   /* Se token existe mas IP é diferente, bloqueia acesso */
                            {
                                _ = _coreDI.LogCore.PMMWpmLgCore(3,
                                   ipaddr,
                                   OriginWebServer,
                                   className,
                                   methodName,
                                   PMCSystmMsgC.PMMmessagecenter(21, 637) + userName + " / " + request.AuthIpaddr,
                                  _coreDI.Configuration);
                                return new PMCSystmWebSrvAuthResp
                                {
                                    AuthCode = (int)WebServerRetCodes.TokenInvalidMismatch,
                                    AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 2)
                                };
                            }                            
                        }
                        /*---------- Cria ou  atualiza entrada da sessão no dicionário de sessão web server ----------*/
                        var websrvSessDict = PMCDataWebSrvSessions.AddToken(tokenresultCheck.AuthTokenOriginal,
                        request.AuthIpaddr,
                        tenantName,
                        userName,
                        subtype,
                        userId);

                        if (websrvSessDict[0] != "0")           // Se retornou erro
                        {
                            _ = _coreDI.LogCore.PMMWpmLgCore(2,
                                   ipaddr,
                                   OriginWebServer,
                                   className,
                                   methodName,
                                   PMCSystmMsgC.PMMmessagecenter(21, 636).Replace("...", "AddToken"),
                                  _coreDI.Configuration);
                            return new PMCSystmWebSrvAuthResp
                            {
                                AuthCode = (int)WebServerRetCodes.InternalError,
                                AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                            };
                        }
                        /*---------------------------------------------------------------------*/
                        /* Grava entrada inicial no Trace                                      */
                        /*---------------------------------------------------------------------*/

                        traceMsg.MsgTxt = PMCSystmMsgC.PMMmessagecenter(0, 54);
                        traceMsg.MsgData = auxAuthCode + " # " +
                            userName;
                        _ = _coreDI.TrcCore.PMMWTrGlobal(traceMsg);

                        return new PMCSystmWebSrvAuthResp
                        {
                            AuthCode = auxAuthCode,
                            AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 7),
                            AuthOtherData = userName
                        };
                   
                }
                /*---------------------------------------------------------------------*/
                /* Grava entrada inicial no Trace                                      */
                /*---------------------------------------------------------------------*/

                traceMsg.MsgTxt = PMCSystmMsgC.PMMmessagecenter(0, 54);
                traceMsg.MsgData = "NoData";
                _ = _coreDI.TrcCore.PMMWTrGlobal(traceMsg);
                return new PMCSystmWebSrvAuthResp
                {
                    AuthCode = auxAuthCode,
                    AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 7),
                    AuthTokenOriginal = tokenresultCheck.AuthTokenOriginal
                };

            }
            catch (Exception ex)
            {
                _ = _coreDI.LogCore.PMMWpmLgCore(1,
                        ipaddr,
                        OriginWebServer,
                        className,
                        methodName,
                        PMCSystmMsgC.PMMmessagecenter(21, 627) + ex.Message,
                       _coreDI.Configuration);

                return new PMCSystmWebSrvAuthResp
                {
                    AuthCode = (int)WebServerRetCodes.InternalError,
                    AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                };
            }
            
        }
        /*-----------------------------------------------------------------*/
        /* CheckToken: Valida token informado no header                    */
        /*-----------------------------------------------------------------*/

        private async Task<PMCSystmWebSrvAuthResp> CheckToken(string token)
        {
            PMCSystmGtkN pmgettoken = new PMCSystmGtkN(_coreDI.Configuration,_coreDI.LogCore, _coreDI.HttpContextAccessor);

            int timetolerance = 5;
            PMCSystmEnDe newende = new PMCSystmEnDe(_coreDI.Configuration);

            // Decriptografa Toekn
            string decryptedToken = newende.Decrypt(
                className,
                "CheckToken",
                ipaddr,
                token
            );


            if (decryptedToken == "#error")
            {
                return new PMCSystmWebSrvAuthResp
                {
                    AuthCode = (int)WebServerRetCodes.InternalError,
                    AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                };
            }

            gtknresult = pmgettoken.PMMChkToken("Webserver",
                decryptedToken,
                tenantName,
                OriginWebServer);

            switch (gtknresult[0])
            {
                case "0":                               /* Ok - Token validado */    
                    break;
                case "1":                       /* Válido mas vai expirar */
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = (int)WebServerRetCodes.TokenWillExpire,
                        AuthMessage = gtknresult[1]
                    };

                case "2":                       /* Expirado */
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = (int)WebServerRetCodes.TokenInvalidExpired,
                        AuthMessage = gtknresult[1]
                    };
                case "3":                       /* Assinatura inválida */
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = (int)WebServerRetCodes.TokenInvalidSignature,
                        AuthMessage = gtknresult[1]
                    };

                default:
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = (int)WebServerRetCodes.InternalError,
                        AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                    };

            }


            return new PMCSystmWebSrvAuthResp
            {
                AuthCode = (int)WebServerRetCodes.OK,
                AuthMessage = gtknresult[1],
                AuthTenant = tenantName, 
                AuthTokenOriginal = decryptedToken
            };

        }
        /*-----------------------------------------------------------------*/
        /* CheckTokenPassword: Valida senha do token informado no header   */
        /*-----------------------------------------------------------------*/

        public PasswordValidationResp CheckTokenPassword(string tokenPassword, string tenantNameFromDb, string userName)
        {
            var resp = new PasswordValidationResp();
            string methodName = "CheckTokenPassword";

            // 1. Validação inicial
            if (string.IsNullOrEmpty(tokenPassword))
            {
                _ = _coreDI.LogCore.PMMWpmLgCore(3,
                                   ipaddr,
                                   OriginWebServer,
                                   className,
                                   methodName,
                                   PMCSystmMsgC.PMMmessagecenter(21, 646) + userName,
                                  _coreDI.Configuration);
                resp.ErrorMessage = PMCSystmMsgC.PMMmessagecenter(59, 6);
                return resp;
            }
            // Instancia PMCSystmEnDe passando IConfiguration e a config já populada
            PMCSystmEnDe newende = new PMCSystmEnDe(_coreDI.Configuration);

            // Agora chama o método Decrypt normalmente
            DecryptedRequestPassword = newende.Decrypt(
                className,
                methodName,
                ipaddr,
                tokenPassword
            );

            if (DecryptedRequestPassword == "#error")
            {
                resp.ErrorMessage = PMCSystmMsgC.PMMmessagecenter(59, 7);
                return resp;
            }
            //  Validação de tamanho total da senha (32)
            if (DecryptedRequestPassword.Length != 32)
            {
                _ = _coreDI.LogCore.PMMWpmLgCore(3,
                                  ipaddr,
                                  OriginWebServer,
                                  className,
                                  methodName,
                                  PMCSystmMsgC.PMMmessagecenter(21, 647)
                                  .Replace("xx", Convert.ToString(tokenPassword.Length)) + userName,
                                 _coreDI.Configuration);
                resp.ErrorMessage = PMCSystmMsgC.PMMmessagecenter(59, 34);
                return resp;
            }
            // Validação de ll (dois primeiros caracteres numéricos)

            string decryptPassword = DecryptedRequestPassword;

            if (!int.TryParse(decryptPassword.Substring(0, 2), out int ll))
            {
               _ = _coreDI.LogCore.PMMWpmLgCore(3,
                                 ipaddr,
                                 OriginWebServer,
                                 className,
                                 methodName,
                                 PMCSystmMsgC.PMMmessagecenter(21, 648)
                                 .Replace("xx", decryptPassword.Substring(0, 2)) + userName,
                                _coreDI.Configuration);
                resp.ErrorMessage = PMCSystmMsgC.PMMmessagecenter(59, 35);
                return resp;
            }

            //  Validação de tamanho do prefixo do nome do tenant  (18)
            if (ll != 16)
            {
                _ = _coreDI.LogCore.PMMWpmLgCore(3,
                                  ipaddr,
                                  OriginWebServer,
                                  className,
                                  methodName,
                                  PMCSystmMsgC.PMMmessagecenter(21, 649)
                                  .Replace("xx",Convert.ToString(ll)) + userName,
                                 _coreDI.Configuration);
                resp.ErrorMessage = PMCSystmMsgC.PMMmessagecenter(59, 34);
                return resp;
            }

            
            // Extração do tenantName da senha
            string tenantNameExtracted = decryptPassword.Substring(2, ll);

            // Comparação com tenantName do BD
            if (!string.Equals(tenantNameExtracted, tenantNameFromDb, StringComparison.OrdinalIgnoreCase))
            {
                _ = _coreDI.LogCore.PMMWpmLgCore(3,
                                  ipaddr,
                                  OriginWebServer,
                                  className,
                                  methodName,
                                  PMCSystmMsgC.PMMmessagecenter(21, 650)
                                  .Replace("...", tenantNameExtracted)
                                  .Replace("+++", tenantNameFromDb) + userName,
                                 _coreDI.Configuration);
                resp.ErrorMessage = PMCSystmMsgC.PMMmessagecenter(59, 36);
                return resp;
            }

            // Se chegou até aqui, está tudo ok
            resp.TenantFromPsw = tenantNameExtracted;
            resp.Success = true;
            return resp;
        }

    }
}