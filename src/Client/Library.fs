namespace Elmish.Remoting
[<RequireQualifiedAccess>]
module Program =
    open Elmish
    open Fable
    open Fable.Core

    let mkRemoteProgram
        (init : 'arg -> 'model * Cmd<Msg<'server,'client>>)
        (update : 'client -> 'model -> 'model * Cmd<Msg<'server,'client>>)
        (view : 'model -> Dispatch<Msg<'server,'client>> -> 'view) =
        { init = init
          update = fun ms md -> match ms with C msg -> update msg md | _ -> md, Cmd.none
          view = view
          setState = fun model -> view model >> ignore
          subscribe = fun _ -> Cmd.none
          onError = Fable.Import.Browser.console.error }

    [<PassGenerics>]
    let runWithWebSocketWith server onConnectionOpen onConnectionLost (arg: 'arg) (program: Program<'arg, 'model, Msg<'server, 'client>, 'view>) =
        let (model,cmd) = program.init arg
        let url = Fable.Import.Browser.URL.Create(Fable.Import.Browser.window.location.href)
        url.protocol <- url.protocol.Replace ("http","ws")
        url.pathname <- server

        let ws = ref None
        let inbox = MailboxProcessor.Start(fun (mb:MailboxProcessor<_>) ->
            let rec loop (state:'model) =
                async {
                    let! msg = mb.Receive()
                    let newState =
                        try
                            match msg with
                            |C msg ->
                                let (model',cmd') = program.update (C msg) state
                                program.setState model' mb.Post
                                cmd' |> List.iter (fun sub -> sub mb.Post)
                                model'
                            | S msg ->
                                !ws |> Option.iter (
                                    fun (s:Fable.Import.Browser.WebSocket) ->
                                        s.send(JsInterop.toJson msg))
                                state
                        with ex ->
                            program.onError ("Unable to process a message:", ex)
                            state
                    return! loop newState
                }
            loop model
        )
        program.setState model inbox.Post
        let rec websocket server r =
            let ws = Fable.Import.Browser.WebSocket.Create server //url.href
            r := Some ws
            ws.onopen <- fun _ ->
                onConnectionOpen |> Option.iter (C >> inbox.Post)
            ws.onclose <- fun _ ->
                onConnectionLost |> Option.iter (C >> inbox.Post)
                Fable.Import.Browser.window.setTimeout(websocket server r, 1000) |> ignore
            ws.onmessage <- fun e ->
                e.data |> string |> JsInterop.ofJson |> C |> inbox.Post
        websocket url.href ws
        let sub =
            try
                program.subscribe model
            with ex ->
                program.onError ("Unable to subscribe:", ex)
                Cmd.none
        sub @ cmd |> List.iter (fun sub -> sub inbox.Post)