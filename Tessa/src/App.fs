namespace Tessa.App
open Tessa.View
open Tessa.View.Types
open Tessa.Solve
open Tessa.Eval
open Tessa.Eval.Types
open Tessa.Lex
open Tessa.Parse
open Tessa.Util

module App = 
    module V = View
    module V = ViewTypes
    module E = Eval 
    module E = EvalTypes
    module S = Solve
    module Lex = Lex 
    module Parse = Parse
    open Util

    open Browser.Dom
    open Fable.Core.JsInterop

    let draw (ctx: Browser.Types.CanvasRenderingContext2D) (shape: V.DrawShape) =
        // printf "%A" shape
        match shape with 
        | V.DrawPoint(point, options) ->
            ctx.beginPath()
            ctx.arc(point.x, point.y, 5.0, 0.0, 3.141592 * 2.0)
            ctx.fillStyle <- !^ options.color
            ctx.closePath()
            ctx.fill()
        | V.DrawSegment(segment, options) -> 
            ctx.beginPath()
            ctx.moveTo(segment.orig.x, segment.orig.y)
            ctx.lineTo(segment.dest.x, segment.dest.y)
            ctx.closePath()
            ctx.strokeStyle <- !^ options.color
            ctx.stroke()
            // ctx.fill()

    let fromResult = function 
        | Ok o -> o 
        | Error e -> failAndPrint e

    let go (ctx: Browser.Types.CanvasRenderingContext2D) program = 
        try 
            let lexed = Lex.lex program 
            // printf "%A" lexed 

            let parsed = lexed |> fromResult |> List.map fst |> Parse.parseList
            // printf "%A" parsed

            let result = parsed |> fromResult |> fst |> E.eval

            // printf "%A" result

            let targets = {V.height = 500.0; V.width = 500.0; V.topLeft = (200.0, 200.0)}

            let (drawable, errs) = V.drawableFromEvalResult result (S.makeSolvers ()) targets
            // printf "%A" drawable
            // printf "%A" errs
            ctx.clearRect(0.0, 0.0, 1000.0, 1000.0)

            List.iter (draw ctx) drawable
        with 
            | _ -> ()

    // Mutable variable to count the number of times we clicked the button
    // let mutable count = 0
    // https://github.com/fable-compiler/fable2-samples/blob/master/browser/src/App.fs
    let mutable myCanvas : Browser.Types.HTMLCanvasElement = unbox window.document.getElementById "canvas" // myCanvas is defined in public/index.html 
    myCanvas.width <- float 1000 
    myCanvas.height <- float 1000
    let ctx = myCanvas.getContext_2d()

    // Test case 1: Value is an Array, so we don't catch GeoExp!! let program = "[] 'a 'b 'c 'd = (:square)"
    let program = 
        """
        [] 'a 'b 'c 'd = (:square); 
        a + b + c + d + a ! 'k;
        """ 

    // go ctx program

    let textArea =  window.document.getElementById "text" :?> Browser.Types.HTMLTextAreaElement
    textArea.onkeydown <- fun event -> 
        if event.keyCode = float 192 
        then 
            event.preventDefault ()
            go ctx textArea.value
        else ()

    // Get a reference to our button and cast the Element to an HTMLButtonElement
    // let myButton = document.querySelector(".my-button") :?> Browser.Types.HTMLButtonElement

    // // Register our listener
    // myButton.onclick <- fun _ ->
    //     count <- count + 1
    //     myButton.innerText <- sprintf "You clicked: %i time(s)" count