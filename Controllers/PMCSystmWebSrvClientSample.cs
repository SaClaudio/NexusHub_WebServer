/*-------------------------------------- Descrição -----------------------------------*/
/*                                                                                    */
/* FUNÇÃO: Web Client de exemplo para o Assinante. Responsável por enviar             */
/*         requisições HTTP ao Web Server NexusHub, incluindo login, validação        */
/*         de Token/Senha e consulta de produtos via protocolo definido.              */
/*                                                                                    */
/* CLASSE: PMCSystmWebSrvClientSample                                                 */
/*                                                                                    */
/* MÉTODOS:                                                                           */
/*         - Main:       Executa o fluxo de login e consulta                          */
/*                       → Configura headers obrigatórios (Function, Token,           */
/*                         Password, Endpoint, Protocolo)                             */
/*                       → Envia requisição GET ao Web Server                         */
/*                       → Interpreta HttpStatusCode retornado                        */
/*                       → Converte resposta JSON em objeto PMCSystmWebSrvResp        */
/*                       → Trata códigos de retorno, incluindo expiração de Token     */
/*                                                                                    */
/* RETORNO:                                                                           */
/*         - Exibe no console o HttpStatusCode, WebSrvRetCode, mensagem e dados       */
/*           retornados pelo Web Server.                                              */
/*                                                                                    */
/* NOTAS:                                                                             */
/*         - Este client é apenas um exemplo de referência para desenvolvedores.      */
/*         - O tratamento de expiração do Token (código 2009) deve ser expandido      */
/*           em produção para refazer login com novo Token e senha.                   */
/*         - Recomenda-se armazenar Token e Password em cofres de segredo.            */
/*         - O client deve ser preparado para novos endpoints futuros.                */
/*                                                                                    */
/*------------------------------------------------------------------------------------*/
namespace NexusHub_WebServer.Controllers
{
    using PriceMaker_SharedLib.Models;
    using System;
    using System.Net.Http;
    using System.Net.Http.Json;
    using System.Threading.Tasks;

    

    class PMCSystmWebSrvClientSample
    {
        static async Task Main()
        {
            using var client = new HttpClient();

            // Headers obrigatórios conforme manual
            client.DefaultRequestHeaders.Add("Function", "Login");
            client.DefaultRequestHeaders.Add("Token", "abc123");
            client.DefaultRequestHeaders.Add("Password", "secret");
            client.DefaultRequestHeaders.Add("Endpoint", "products");
            client.DefaultRequestHeaders.Add("Protocolo", "01|7891234567890"); // consulta por SKU

            try
            {
                var response = await client.GetAsync("https://localhost:8001/PMCSystmWebServer");

                Console.WriteLine("HttpStatusCode: " + (int)response.StatusCode);

                if (response.IsSuccessStatusCode)
                {
                    var resp = await response.Content.ReadFromJsonAsync<PMCSystmWebSrvResp>();

                    Console.WriteLine($"WebSrvRetCode: {resp.WebSrvRetCode}");
                    Console.WriteLine($"WebSrvRetMessage: {resp.WebSrvRetMessage}");
                    Console.WriteLine($"WebSrvRetData: {resp.WebSrvRetData}");

                    // Tratamento especial para expiração do Token
                    if (resp.WebSrvRetCode == 2009)
                    {
                        Console.WriteLine("Token expirado. É necessário gerar novo Token e refazer login.");
                    }
                }
                else
                {
                    Console.WriteLine("Erro na requisição: " + response.ReasonPhrase);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exceção: " + ex.Message);
            }
        }
    }
}
