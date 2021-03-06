module GiraffeRazorSample

open System
open System.IO
open System.Threading
open Microsoft.AspNetCore
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.Mvc.ModelBinding
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open FSharp.Control.Tasks.V2.ContextInsensitive
open Giraffe
open Giraffe.Razor

// ---------------------------------
// Models
// ---------------------------------

[<CLIMutable>]
type Person =
    {
        Name : string
    }

[<CLIMutable>]
type CreatePerson =
    {
        Name    : string
        CheckMe : bool
    }

// ---------------------------------
// Web app
// ---------------------------------

let antiforgeryTokenHandler =
    text "Bad antiforgery token"
    |> RequestErrors.badRequest
    |> validateAntiforgeryToken

let bytesToKbStr (bytes : int64) =
    sprintf "%ikb" (bytes / 1024L)

let displayFileInfos (files : IFormFileCollection) =
    files
    |> Seq.fold (fun acc file ->
        sprintf "%s\n\n%s\n%s" acc file.FileName (bytesToKbStr file.Length)) ""
    |> text

let smallFileUploadHandler =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            return!
                (match ctx.Request.HasFormContentType with
                | false -> text "Bad request" |> RequestErrors.badRequest
                | true  -> ctx.Request.Form.Files |> displayFileInfos) next ctx
        }

let largeFileUploadHandler =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            let formFeature = ctx.Features.Get<IFormFeature>()
            let! form = formFeature.ReadFormAsync CancellationToken.None
            return! (form.Files |> displayFileInfos) next ctx
        }

let renderPerson =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            let name =
                match ctx.TryGetQueryStringValue "name" with
                | Some n -> n
                | None -> "Razor"
            let model = { Name = name }
            let viewData = dict [("Title", box "Mr Fox")]
            return! razorHtmlView "Person" (Some model) (Some viewData) None next ctx
        }

let renderCreatePerson =
    let model = { Name = ""; CheckMe = true }
    let viewData = dict [("Title", box "Create peson")]
    razorHtmlView "CreatePerson" (Some model) (Some viewData) None

let createPerson =
    fun (next : HttpFunc) (ctx : HttpContext) ->
        task {
            let modelState = ModelStateDictionary()
            let! model = ctx.BindModelAsync<CreatePerson>()
            if not model.CheckMe then
                modelState.AddModelError("CheckMe", "Checkbox must be checked")
                modelState.AddModelError(String.Empty, "Error without an associated field")
            if String.IsNullOrWhiteSpace(model.Name) then modelState.AddModelError("Name", "Name is rquired")
            if modelState.IsValid then
                let url = sprintf "/person?name=%s" model.Name
                return! redirectTo false url next ctx
            else
                let viewData = dict [("Title", box "Create peson")]
                return! razorHtmlView "CreatePerson" (Some model) (Some viewData) (Some modelState) next ctx
        }

let viewData =
    dict [
        "Who", "Foo Bar" :> obj
        "Foo", 89 :> obj
        "Bar", true :> obj
    ]

let webApp =
    choose [
        GET >=>
            choose [
                route  "/"              >=> text "index"
                route  "/razor"         >=> razorView "text/html" "Hello" None (Some viewData) None
                route  "/person/create" >=> renderCreatePerson
                route  "/person"        >=> renderPerson
                route  "/upload"        >=> razorHtmlView "FileUpload" (Some "File upload") (Some viewData) None
            ]
        POST >=>
            choose [
                route "/small-upload"  >=> smallFileUploadHandler
                route "/large-upload"  >=> largeFileUploadHandler
                route "/person/create" >=> antiforgeryTokenHandler >=> createPerson
            ]
        text "Not Found" |> RequestErrors.notFound ]

// ---------------------------------
// Error handler
// ---------------------------------

let errorHandler (ex : Exception) (logger : ILogger) =
    logger.LogError(EventId(), ex, "An unhandled exception has occurred while executing the request.")
    clearResponse >=> ServerErrors.INTERNAL_ERROR (text ex.Message)

// ---------------------------------
// Main
// ---------------------------------

let configureApp (app : IApplicationBuilder) =
    app.UseGiraffeErrorHandler(errorHandler)
       .UseStaticFiles()
       .UseAuthentication()
       .UseGiraffe webApp

let configureServices (services : IServiceCollection) =
    let sp  = services.BuildServiceProvider()
    let env = sp.GetService<IWebHostEnvironment>()
    services.AddGiraffe() |> ignore
    Path.Combine(env.ContentRootPath, "Views")
    |> services.AddRazorEngine
    |> ignore

let configureLogging (loggerBuilder : ILoggingBuilder) =
    loggerBuilder.AddFilter(fun lvl -> lvl.Equals LogLevel.Error)
                 .AddConsole()
                 .AddDebug() |> ignore

[<EntryPoint>]
let main _ =
    let contentRoot = Directory.GetCurrentDirectory()
    let webRoot     = Path.Combine(contentRoot, "WebRoot")
    WebHost.CreateDefaultBuilder()
        .UseWebRoot(webRoot)
        .Configure(Action<IApplicationBuilder> configureApp)
        .ConfigureServices(configureServices)
        .ConfigureLogging(configureLogging)
        .Build()
        .Run()
    0