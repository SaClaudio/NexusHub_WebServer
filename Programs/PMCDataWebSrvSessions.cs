/*-------------------------------------- Descrição -----------------------------------*/
/*                                                                                    */
/* FUNÇÃO: Dicionário centralizador de dados fixos de sessão do Web Server da         */
/*         plataforma NexusHub. Responsável por manter em memória dados que evitam    */
/*         necessidade de operações de I/O na função "process" do Web Server.         */
/*         Também acumula métricas de tráfego (SessionIn, SessionOut, RequestsCount)  */
/*         que são descarregadas em tabela MySQL no logout ou em novo login.          */
/*                                                                                    */
/* CLASSE: PMCDataWebSrvSessions                                                      */
/*                                                                                    */
/* MÉTODOS:                                                                           */
/*                                                                                    */
/*   - AddToken:                                                                      */
/*       Cria ou atualiza entrada do token no dicionário, salva dados do assinante    */
/*       e descarrega métricas antigas antes de reiniciar contadores.                 */
/*                                                                                    */
/*   - DelToken:                                                                      */
/*       Remove entrada do token no dicionário e descarrega métricas em MySQL.        */
/*                                                                                    */
/*   - GetTenantnm:                                                                   */
/*       Retorna o tenant associado ao token.                                         */
/*                                                                                    */
/*   - IsSessionActive:                                                               */
/*       Verifica se a sessão está ativa (não expirada) com base em LastActivity.     */
/*                                                                                    */
/*   - IncrementRequest:                                                              */
/*       Atualiza LastActivity e incrementa métricas de tráfego.                      */
/*                                                                                    */
/*   - Logout:                                                                        */
/*       Remove completamente a sessão e descarrega métricas em MySQL.                */
/*                                                                                    */
/*   - FlushSessionToDatabase:                                                        */
/*       Persiste métricas de sessão em tabela MySQL.                                 */
/*                                                                                    */
/*   - TokenExists:                                                                   */
/*       Verifica se o token informado existe no dicionário. Se não existir,          */
/*       significa que o login não foi realizado ou já foi encerrado.                 */
/*                                                                                    */
/*------------------------------------------------------------------------------------*/


using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace PriceMaker_MultTenant.Programs
{
    public static class PMCDataWebSrvSessions
    {
        public class Session
        {
            public string? ReturnCode { get; set; }             // código de retorno padrão para respostas (ex: "0" para sucesso, "1" para erro, etc.)
            public string UserId { get; set; }                  // extraído do header do token
            public string UserName { get; set; }          
            public string SessionIpAddr { get; set; }
            public string Tenantnm { get; set; }
            public int Subscribertyp { get; set; }
            public string SubscriberStatus { get; set; }        // ativo, suspenso, cancelado
            public string SubscriberConfigValues { get; set; }  // string JSON com configurações específicas do assinante
            public DateTime LastActivity { get; set; }          // controle de expiração
            public int SessionIn { get; set; }                  // total de requests recebidos
            public int SessionOut { get; set; }                 // total de mensagens retornadas
        }

        public static ConcurrentDictionary<string, Session> WebSrvSessions
            = new ConcurrentDictionary<string, Session>();

        /*-------------------------------------------------------------------------------------------*/
        /* AddToken: cria ou atualiza entrada no dicionário pelo token.                              */
        /* Se não existe, cria nova sessão. Se já existe, descarrega métricas antigas em MySQL,      */
        /* atualiza dados do assinante e reinicia contadores de SessionIn/SessionOut.                */
        /*-------------------------------------------------------------------------------------------*/
        public static List<string> AddToken(string token,
                    string ipaddr,
                    string tenantnm,
                    string username,
                    int subtype,
                    string userId,
                    string subconfigdata) 
        {
            try
            {
                WebSrvSessions.AddOrUpdate(token,
                    new Session
                    {
                        SessionIpAddr = ipaddr,
                        Tenantnm = tenantnm,
                        Subscribertyp = subtype,
                        SubscriberStatus = null,
                        LastActivity = DateTime.UtcNow,
                        SessionIn = 0,
                        SessionOut = 0,
                        UserId = userId,
                        UserName = username,
                        SubscriberConfigValues = subconfigdata
                    },
                    (key, old) =>
                    {
                        FlushSessionToDatabase(key, old);
                        old.SessionIpAddr = ipaddr;
                        old.Tenantnm = tenantnm;
                        old.Subscribertyp = subtype;
                        old.SubscriberStatus = null;
                        old.LastActivity = DateTime.UtcNow;
                        old.SessionIn = 0;
                        old.SessionOut = 0;
                        old.UserId = userId;
                        old.UserName = username;
                        old.SubscriberConfigValues = subconfigdata;
                        return old;
                    });

                return new List<string> { "0", "OK" };
            }
            catch (Exception ex)
            {
                return new List<string> { "9", ex.Message };
            }
        }
        /*-------------------------------------------------------------------------------------------*/
        /* TokenExists: verifica se o token informado já está registrado no dicionário de sessões.   */
        /* Retorna true se o token existe, false caso contrário.                                     */
        /* Uso: validar rapidamente se uma sessão está ativa para determinado token.                 */
        /*-------------------------------------------------------------------------------------------*/
        public static bool TokenExists(string token)
        {
            return WebSrvSessions.ContainsKey(token);
        }

        /*-------------------------------------------------------------------------------------------*/
        /* IpExists: verifica se há alguma sessão associada ao endereço IP informado.                */
        /* Retorna true se o IP já está vinculado a alguma sessão, false caso contrário.             */
        /* Uso: detectar se um mesmo IP está em uso em múltiplas sessões ou controlar concorrência.  */
        /*-------------------------------------------------------------------------------------------*/
        public static bool IpExists(string ipaddr)
        {
            return WebSrvSessions.Values.Any(s => s.SessionIpAddr == ipaddr);
        }

        /*-------------------------------------------------------------------------------------------*/
        /* ValidateTokenAndIp: verifica se o token existe e se o IP atual corresponde ao registrado. */
        /* Retorna true se token e IP são válidos juntos, false caso contrário.                      */
        /* Uso: proteger contra uso indevido de token em IP diferente (session hijacking/replay).    */
        /*-------------------------------------------------------------------------------------------*/
        public static bool ValidateTokenAndIp(string token, string ipaddr)
        {
            if (WebSrvSessions.TryGetValue(token, out Session session))
            {
                return session.SessionIpAddr == ipaddr;
            }
            return false;
        }
        
        /*-------------------------------------------------------------------------------------------*/
        /* DelToken: remove entrada correspondente ao token no dicionário.                           */
        /* Antes de remover, descarrega métricas acumuladas em MySQL.                                */
        /*-------------------------------------------------------------------------------------------*/
        public static List<string> DelToken(string token)
        {
            try
            {
                if (WebSrvSessions.TryRemove(token, out Session removido))
                {
                    FlushSessionToDatabase(token, removido);
                    return new List<string> { "0", "OK" }; // sucesso
                }
                else
                {
                    return new List<string> { "1", "Notfound" }; // fnão existe
                }
            }
            catch (Exception ex)
            {
                return new List<string> { "9", ex.Message }; // falha/crash com mensagem
            }
        }
        
        /*-------------------------------------------------------------------------------------------*/
        /* IsSessionActive: verifica se a sessão está ativa com base em LastActivity.                */
        /* Retorna true se a diferença entre agora e LastActivity for menor que limiteMinutos.       */
        /*-------------------------------------------------------------------------------------------*/
        public static List<string> IsSessionActive(string token, int limiteMinutos)
        {
            try
            {
                if (WebSrvSessions.TryGetValue(token, out Session session))
                {
                    bool ativo = (DateTime.UtcNow - session.LastActivity).TotalMinutes < limiteMinutos;
                    return new List<string> { "0", ativo ? "true" : "false" }; // sucesso
                }
                else
                {
                    return new List<string> { "1", "Notfound" }; // sessão não encontrada
                }
            }
            catch (Exception ex)
            {
                return new List<string> { "9", ex.Message }; // falha/crash com mensagem
            }
        }

        /*-------------------------------------------------------------------------------------------*/
        /* IncrementRequest: atualiza LastActivity e incrementa contador de SessionIn.               */
        /* Usado em cada requisição "Process" para acumular total de requests recebidos.             */
        /*-------------------------------------------------------------------------------------------*/
        public static List<string> IncrementRequest(string token)
        {
            try
            {
                if (WebSrvSessions.TryGetValue(token, out Session session))
                {
                    session.LastActivity = DateTime.UtcNow;
                    session.SessionIn++;
                    return new List<string> { "0", "OK" }; // sucesso
                }
                else
                {
                    return new List<string> { "1", "Notfound" }; // sessão não encontrada
                }
            }
            catch (Exception ex)
            {
                return new List<string> { "9", ex.Message }; // falha/crash com mensagem
            }
        }

        /*-------------------------------------------------------------------------------------------*/
        /* IncrementResponse: incrementa contador de SessionOut.                                     */
        /* Usado sempre que o Web Server retorna uma resposta ao client.                             */
        /*-------------------------------------------------------------------------------------------*/
        public static List<string> IncrementResponse(string token)
        {
            try
            {
                if (WebSrvSessions.TryGetValue(token, out Session session))
                {
                    session.SessionOut++;
                    return new List<string> { "0", "OK" }; // sucesso
                }
                else
                {
                    return new List<string> { "1", "Notfound" }; // sessão não encontrada
                }
            }
            catch (Exception ex)
            {
                return new List<string> { "9", ex.Message }; // falha/crash com mensagem
            }
        }

        /*-------------------------------------------------------------------------------------------*/
        /* Logout: remove completamente a sessão do dicionário.                                      */
        /* Antes de remover, descarrega métricas acumuladas em MySQL.                                */
        /*-------------------------------------------------------------------------------------------*/
        public static List<string> Logout(string token)
        {
            try
            {
                if (WebSrvSessions.TryRemove(token, out Session session))
                {
                    FlushSessionToDatabase(token, session);
                    return new List<string> { "0", "OK" }; // sucesso
                }
                else
                {
                    return new List<string> { "1", "Notfound" }; // não existe
                }
            }
            catch (Exception ex)
            {
                return new List<string> { "9", ex.Message }; // falha/crash com mensagem
            }
        }

        /*-------------------------------------------------------------------------------------------*/
        /* FlushSessionToDatabase: persiste métricas de sessão em tabela MySQL.                      */
        /* Deve ser implementado com INSERT/UPDATE conforme modelo de dados definido.                */
        /*-------------------------------------------------------------------------------------------*/
        private static void FlushSessionToDatabase(string token, Session session)
        {
            // Implementar lógica de persistência em MySQL
        }
        /*-------------------------------------------------------------------------------------------*/
        /* GetSession: retorna o objeto Session associado ao token informado no dicionário.          */
        /* Retorna o objeto completo da sessão se existir, ou null caso o token não esteja registrado*/
        /* Uso: obter diretamente os dados da sessão (UserId, Tenant, Status, etc.) para validações  */
        /*      e controles adicionais sem precisar verificar existência separadamente.              */
        /*-------------------------------------------------------------------------------------------*/
        public static Session GetSession(string token)
        {
            if (WebSrvSessions.TryGetValue(token, out var sessionData))
            {
                sessionData.ReturnCode = "0"; // encontrado
                return sessionData;
            }

            // não encontrado: cria um objeto Session apenas com ReturnCode = "1"
            return new Session
            {
                ReturnCode = "1"
            };
        }


    }
}