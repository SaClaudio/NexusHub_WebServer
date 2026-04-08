/*-------------------------------------- Descrição -----------------------------------*/
/*                                                                                    */
/* FUNÇÃO: Responsável por validar autenticação das requisições recebidas pelo        */
/*         Web Server do NexusHub. Centraliza a lógica de verificação dos headers     */
/*         obrigatórios (Token, Password, Endpoint, Protocolo) e confronta credenciais*/
/*         com os dados do Tenant.                                                    */
/*                                                                                    */
/* CLASSE: PMCSystmWebSrvAuthValidator                                                */
/*                                                                                    */
/* MÉTODOS:                                                                           */
/*         - ValidateAsync: Executa validação completa da requisição                  */
/*                          → Verifica presença dos headers obrigatórios              */
/*                          → Confronta Token e Password com dados do Tenant          */
/*                          → Valida Endpoint e Protocolo                             */
/*                          → Retorna objeto PMCSystmWebSrvAuthResp com resultado     */
/*                                                                                    */
/* RETORNO:                                                                           */
/*         - Objeto PMCSystmWebSrvAuthResp contendo código de status e mensagem       */
/*           de validação (sucesso ou erro).                                          */
/*                                                                                    */
/* NOTAS:                                                                             */
/*         - Controller apenas monta o DTO PMCSystmWebSrvRequest e delega a validação */
/*           ao AuthValidator.                                                        */
/*         - Toda a lógica de autenticação fica centralizada nesta classe.            */
/*         - Instanciação direta (sem DI) garante controle explícito do fluxo.        */
/*         - Facilita manutenção e evolução futura (novos headers ou regras).         */
/*         - Códigos de retorno mapeados em PMCSYStmConstants -> WebServerRetCodes    */
/*           e retornados na DTO PMCSystmWebSrvAuthResp                               */
/*                                                                                    */
/*------------------------------------------------------------------------------------*/

namespace NexusHub_WebServer.Controllers
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
                    Origem = PMCSystmConstants.OriginWebServer,
                    IpAddr = Environment.MachineName,     // Não usa IP e sim, nome do servidor, já que é um processo interno
                    MainObj = className,                  // Nome desta Classe
                    SecObj = methodName,                  // Nome do Metodo
                    Emoji = "✅",                        //  Emoji Padrão
                    MsgTxt = PMCSystmMsgC.PMMmessagecenter(0, 53),
                    MsgData = request.AuthFunction + " # " +
                    request.AuthToken + " # " +
                    request.AuthPassword + " # " +
                    request.AuthEndpoint + " # " +
                    request.AuthProtocol + " # " +
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
                        AuthCode = (int)WebServerRetCodes.LoginRequerid,
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
                /*--- Verifica se tem Senha no Header ---*/
                if (string.IsNullOrEmpty(request.AuthPassword))
                {
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = (int)WebServerRetCodes.PasswordMissing,
                        AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 2)
                    };
                }

                /*--- Verifica se tem Endpoint no Header ---*/
                if (string.IsNullOrEmpty(request.AuthEndpoint))
                {
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = (int)WebServerRetCodes.EndpointMissing,
                        AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 3)
                    };
                }

                /*--- Verifica se tem Protocol no Header ---*/
                if (string.IsNullOrEmpty(request.AuthProtocol))
                {
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = (int)WebServerRetCodes.ProtocolMissing,
                        AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 1)
                    };
                }
                /*--------- Faz avaliação Cross Headers */

                if (functionlower != "login" && !string.IsNullOrEmpty(request.AuthPassword))
                {
                    return new PMCSystmWebSrvAuthResp
                    {
                        AuthCode = (int)WebServerRetCodes.CrossHeaderRejtPsw,
                        AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 1)
                    };

                }

                /*----------- Encaminha atendimento da função passada em Function -------------*/
                var tokenresultCheck = new PMCSystmWebSrvAuthResp();

                switch (functionlower)
                {
                    case "login":               // Função "Login"

                        /*--------------------------------- Valida senha informada no request --------------------------------*/
                        /* Composição da senha: tamanho do tenantName + tenantName (completo) + dataHora que foi criada no UI */
                        /*----------------------------------------------------------------------------------------------------*/
                        // Instancia PMCSystmEnDe passando IConfiguration e a config já populada
                        PMCSystmEnDe newende = new PMCSystmEnDe(_coreDI.Configuration);

                        // Agora chama o método Decrypt normalmente
                        DecryptedRequestPassword = newende.Decrypt(
                            className,
                            methodName,
                            ipaddr,
                            request.AuthPassword
                        );
                        

                        if (DecryptedRequestPassword == "#error")
                        {
                            return new PMCSystmWebSrvAuthResp
                            {
                                AuthCode = (int)WebServerRetCodes.InternalError,
                                AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                            };
                        }

                        int lentntnm = Convert.ToInt32(DecryptedRequestPassword.Substring(0, 2));   /*-- tamanho do prefixo do nome do Tenant ---*/
                        tenantName = DecryptedRequestPassword.Substring(2, lentntnm);
                        dbParm = "0" + "‡" + tenantName;

                        var(resultData, resultObjt) = await driverIO.PMMIOdriver(dbParm,
                            className,
                            methodName,
                            PMCSystmConstants.OriginWebServer);

                        if (resultData[0] != "0")       /* Se deu erro no acesso ao mysql/tenant */
                        {
                            return new PMCSystmWebSrvAuthResp
                            {
                                AuthCode = (int)WebServerRetCodes.InternalError,
                                AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                            };
                        }
                        if (resultData[1] == "0")       /* Codigo de retorno normal porém , sem dados. Erro de sincronismo. Impossível */
                        {
                            _ = _coreDI.LogCore.PMMWpmLgCore(2,
                                ipaddr,
                                PMCSystmConstants.OriginWebServer,
                                className,
                                methodName,
                                PMCSystmMsgC.PMMmessagecenter(21, 622) + tenantName,
                                _coreDI.Configuration);
                            return new PMCSystmWebSrvAuthResp
                            {
                                AuthCode = (int)WebServerRetCodes.InternalError,
                                AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                            };
                        }

                        DecryptedRequestPasswordDb = newende.Decrypt("PMCWebserver", "GetAsync", "???.?.?.?", resultData[2]);

                        if (DecryptedRequestPasswordDb == "#error")
                        {
                            return new PMCSystmWebSrvAuthResp
                            {
                                AuthCode = (int)WebServerRetCodes.InternalError,
                                AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                            };
                        }

                        if (DecryptedRequestPassword != DecryptedRequestPasswordDb)         /* Se senha no request x tentant não bater */
                        {
                            return new PMCSystmWebSrvAuthResp
                            {
                                AuthCode = (int)WebServerRetCodes.PasswordMismatch,
                                AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 6)
                            };
                        }
                        /*--------- Senha está ok. Valida token informado no request ---*/

                        string tokenatDB = resultData[8];          // Token armazenado na BD de configuraç~so do assinante
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

                        if (tokenresultCheck.AuthTokenOriginal != tokenatDB)        /* Se token não é igual ao gerado anteriormente */
                        {
                            return new PMCSystmWebSrvAuthResp
                            {
                                AuthCode = (int)PMCSystmConstants.WebServerRetCodes.TokenInvalidMismatch,
                                AuthMessage = gtknresult[2]
                            };
                        }

                        /*---------- Acessa assinante em Identity pelo keyid que vem no header do token -----------*/


                        var _identityIO = new PMCSystmIdentityIO(_coreDI);
                        dbParm = "1" + "‡" + userId;

                        // Chama o driver de Identity
                        var (identityuser, identityrc) = await _identityIO.PMMIOdriver(dbParm,
                            className,
                            methodName,
                            PMCSystmConstants.OriginWebServer);
                        string userName = string.Empty;

                        switch (identityrc.ReturnCode)
                        {
                            case "0":     /* Ok, usuário encontrado e ativo */
                                userName = identityrc.identityUsr_UserName;         // UserName (normalmente email)
                                break;
                            case "1":     /* Usuário não encontrado */
                                _ = _coreDI.LogCore.PMMWpmLgCore(3,
                                    ipaddr,
                                    PMCSystmConstants.OriginWebServer,
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
                                   PMCSystmConstants.OriginWebServer,
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
                                   PMCSystmConstants.OriginWebServer,
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
                                       PMCSystmConstants.OriginWebServer,
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
                                   PMCSystmConstants.OriginWebServer,
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
                                   PMCSystmConstants.OriginWebServer,
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
                        
                        string ipaddrDict = PMCDataWebSrvSessions.GetIpByToken(request.AuthToken);

                        if (!string.IsNullOrEmpty(ipaddrDict))          // Se já existe um token registrado, verifica se IP é o mesmo. Se for o mesmo, mantém sessão. Se for diferente, bloqueia acesso (possível roubo de token)
                        {
                            if (!PMCDataWebSrvSessions.ValidateTokenAndIp(request.AuthToken, request.AuthIpaddr))   /* Se token existe mas IP é diferente, bloqueia acesso */
                            {
                                _ = _coreDI.LogCore.PMMWpmLgCore(3,
                                   ipaddr,
                                   PMCSystmConstants.OriginWebServer,
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
                            /*---------- Cria ou  atualiza entrada da sessão no dicionário de sessão web server ----------*/
                            var websrvSessDict = PMCDataWebSrvSessions.AddToken(request.AuthToken,
                            request.AuthIpaddr,
                            tenantName,
                            userName,
                            subtype,
                            userId);

                            if (websrvSessDict[0] != "0")           // Se retornou erro
                            {
                                _ = _coreDI.LogCore.PMMWpmLgCore(2,
                                       ipaddr,
                                       PMCSystmConstants.OriginWebServer,
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
                            break;

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
                    
                    case "process":             // Função "process"
                                                // 🔹 Garante que ExtConfig está populado
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
                        break;
                }
                return new PMCSystmWebSrvAuthResp
                {
                    AuthCode = auxAuthCode,
                    AuthMessage = PMCSystmMsgC.PMMmessagecenter(59, 7)
                };

            }
            catch (Exception ex)
            {
                _ = _coreDI.LogCore.PMMWpmLgCore(1,
                        ipaddr,
                        PMCSystmConstants.OriginWebServer,
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
                PMCSystmConstants.OriginWebServer);

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
    }
}