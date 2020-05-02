namespace Tessa.Eval

open Tessa.Language
open Tessa.Solve
open Tessa.Parse
open Tessa.Util
open Tessa.Eval.Types
open Tessa.Eval.PrimitiveProcedures

// todo: make sure Parse and Solve both return Result
module Eval = 
    module P = Parse
    module L = Language
    module S = Solve
    open Util
    open EvalTypes
    open PrimitiveProcedures

    type StackExecutionContext = {
        currentOp: OperationState;
        arguments: Exp list;
        environment: Environment;
        draw: DrawMap;
        ret: Exp option;
    }

    type ExecutionContext = {
        // stackContext[i] has continuation stackContext[i + 1]
        continuations: StackExecutionContext list;
        currentContext: StackExecutionContext;
        solveContext: S.SolveContext;
        reduction: Exp option;
    }

    let startingEnvironment : Environment= 
        Map.empty 
        |> Map.add "plus" (PrimitiveProcedure AddNumber)
        |> Map.add "square" (PrimitiveProcedure Square)
        |> Map.add "c4-clockwise" (PrimitiveProcedure C4Clockwise)

    let emptyStackExecutionContext = {
        currentOp = Empty;
        arguments = [];
        environment = startingEnvironment;
        draw = Map.empty;
        ret = None;}

    let emptyExecutionContext = {
        continuations = [];
        currentContext = emptyStackExecutionContext;
        solveContext = S.emptySolveContext;
        reduction = None;
    }

    let updateTop f context = 
        {context with currentContext = (f context.currentContext)}

    let liftToExecutionContext 
        (f: StackExecutionContext -> Result<StackExecutionContext, EvalError>) 
        : ExecutionContext -> Result<ExecutionContext, EvalError>  = fun stackContext ->
            result {
                let! newContext = f stackContext.currentContext
                return {stackContext with currentContext = newContext}
            }

    let acceptExpressionHelper exp context = 
        match context.currentOp with
            | EmptyAcceptNext -> 
                match exp with 
                | PrimitiveProcedure p -> Ok <| (context.arguments, Op (Primitive p))
                | _ -> Error <| ApplyingNonFunction exp
            | _ -> Ok <| (exp :: context.arguments, context.currentOp)

    let acceptExpression exp context = 
        result {
            let! (newArgs, newOp) = acceptExpressionHelper exp context.currentContext
            return updateTop (fun t -> {t with arguments = newArgs; currentOp = newOp}) context
        }

    let acceptNextOp context = updateTop (fun t -> {t with currentOp = EmptyAcceptNext;}) context 

    let mergeDraws = Map.unionWith (@)

    let reduceStackHelper context = 
        match context.currentOp with 
            | Empty -> Ok (List.tryHead context.arguments, context.arguments, context.currentOp, context.environment, context.draw)

            | EmptyAcceptNext -> Ok (List.tryHead context.arguments, context.arguments, context.currentOp, context.environment, context.draw) 

            | Op o -> 
                match o with 
                | Primitive p -> result {
                    let fn = lookupPrimitiveProcedure p
                    let! (applied, message) = fn (List.rev context.arguments) context.environment
                    let (newDraw, newEnv) = 
                        match message with
                        | Some(AugmentEnvironment e) -> (context.draw, Map.union e context.environment)
                        | Some(DrawGeo(key, draws)) -> (mergeDraws (Map.add key [draws] Map.empty) context.draw, context.environment)
                        | None -> (context.draw, context.environment)
                    return (Some applied, [applied], Empty, newEnv, newDraw)
                }

    let reduceStack context = result {
        let! (ret, args, op, env, draw) = reduceStackHelper context.currentContext
        return updateTop (fun t -> {t with ret = ret; arguments = args; currentOp = op; environment = env; draw = draw}) context
    }

    let returnToLastContinuation ret initContext topDraw =
            // return to the last continuation
        match List.tryHead initContext.continuations with 
        | None -> Error UnbalancedParenExtraClose 
        | Some continuation -> 
            let newDraw = mergeDraws topDraw continuation.draw
            let updatedCurrentContext =  {continuation with arguments = tryCons ret continuation.arguments; draw = newDraw}
            let newContext = {initContext with continuations = List.tail initContext.continuations; currentContext = updatedCurrentContext;}
            Ok newContext

    let findIdentifier execContext ident = 
        match Map.tryFind ident execContext.currentContext.environment with
            | None -> Error <| UndefinedVariable(ident, "If you meant to assign, assign to a symbol.")
            | Some exp -> Ok exp

    let rec evalWord context exp  =
        match exp with
        | P.Number n -> Ok <| Number n
        | P.Identifier ident -> findIdentifier context ident 
        | P.PrimitiveProcedure p -> parsePrimitiveToEvalPrimitive p |> PrimitiveProcedure |> Ok
        | P.Quote q -> Ok <| Quote q

    type StackAction = 
        | BeginNewStack
        | ReturnNewStack
        | Expression of P.Word 
        | EndStack
        | ReduceAndPushOp of P.PrimitiveProcedure option

    let flattenParseStackCommands commands = 
        let rec flatten command = 
            match command with 
            // oof that append at the end is horribly inefficient
            | P.NewStack cs -> BeginNewStack :: (List.collect flatten cs) @ [ReturnNewStack]
            | P.Expression word -> [Expression word]
            | P.ReduceAndPushOp(x) -> [ReduceAndPushOp x]
            | P.EndStack -> [EndStack]
        List.collect flatten commands

    let newTopFrame context = 
        {emptyStackExecutionContext with environment = context.currentContext.environment}

    let pushNewTopFrame context frame = 
        let current = context.currentContext 
        {context with currentContext = frame; continuations = current :: context.continuations;}

    let evalStackCommand initContext stackCommand = 
        match stackCommand with 
        | BeginNewStack -> 
            newTopFrame initContext |> pushNewTopFrame initContext |> Ok

        | Expression word -> 
            evalWord initContext word |> Result.bind (flip acceptExpression initContext)

        | EndStack -> 
            reduceStack initContext |> Result.map (updateTop (fun t -> {t with arguments= []; currentOp = Empty;}))

        | ReduceAndPushOp(maybePrimitive) -> 
            // todo: reduce is possible
            match maybePrimitive with
            | Some primitive -> 
                result {
                    let! acceptingContext = initContext |> reduceStack |> Result.map acceptNextOp 
                    let evalPrimitive = parsePrimitiveToEvalPrimitive primitive |> PrimitiveProcedure
                    return! acceptExpression evalPrimitive acceptingContext
                }
            | None -> initContext |> reduceStack |> Result.map acceptNextOp // liftToExecutionContext (reduceStack >=> acceptNextOp) initContext

        | ReturnNewStack -> 
            result {
                let! (ret, _, _, _, topDraw) = reduceStackHelper initContext.currentContext
                return! returnToLastContinuation ret initContext topDraw
            }

    let firstPriorityOption newReduction lastResult = 
        match (newReduction, lastResult) with
        | (Some (x, nr), Some (y, lr)) -> 
            if x <= y then Some (x, nr) else Some (y, lr)
        | (Some (x, nr), None) -> Some (x, nr)
        | (None, Some (y, lr)) -> Some (y, lr)
        | (None, None) -> None 

    // Like a sauce in a pot all day!
    let tryReduceDown context = 
        let pop context = 
            let nextContinuation = List.tryHead context.continuations
            Option.map (fun c -> {context with currentContext = c; continuations = List.tail context.continuations}) nextContinuation

        let toOption result = 
            match result with
            | Ok o -> Some o
            | Error _ -> None

        let joinPriority p = 
            match p with 
            | None -> None 
            | Some (priority, exp) -> Option.map (fun e -> (priority, e)) exp

        // This is really a mess right now.
        // Lower priority is better; lowest priority is always the top frame.
        let rec go invPriority context  = 
            match context.continuations with 
            // If there are no continuations, it's easy -- reduce the top and only frame. This may or may not yield a value.
            | [] -> 
                let reduced = reduceStackHelper context.currentContext |> toOption |> Option.map (fun (ret, _, _, _,_) -> ret) |> Option.flatten
                Option.map (fun r -> (invPriority, r)) reduced

            // As an example for when we have continuations, let's use 
            // 2 :plus 3 :plus ('i = 
            // The partial evaluation should be 5, because we give up on ('i =
            // and go to the last continuation.
            | _::_ ->  
                // First we get the fallback by popping the top frame and recursively getting the result
                // as if the top frame never existed.
                let fallBack = pop context |> Option.map (go (invPriority + 1)) |> Option.flatten
                let withThisFrameResult = result {
                    // Then we reduce the top frame and push to the continuation and try reducing.
                    let! (ret, _, _, _, topDraw) = reduceStackHelper context.currentContext 
                    let! returnedContext = returnToLastContinuation ret context topDraw
                    return go invPriority returnedContext
                } 

                let withThisFrame = toOption withThisFrameResult |> Option.flatten
                // This lets us compare two things below:
                // The continuation reduced without this frame, and the continuation reduced with it.
                // If the introduction of this frame still yields a good result, we prefer that.
                // But otherwise, we'll go with the fallback (if it's None, we lose nothing; if it's
                // Some, that means the current frame is incomplete and introducing an error)
                firstPriorityOption withThisFrame fallBack

        go 0 context

    let eval stackCommands =
        let rec go context commands lastResult = 
            match commands with 
            | [] -> (lastResult, Ok context)
            | command :: rest -> 
                match evalStackCommand context command with 
                | Ok newContext -> 
                    let newReduction = tryReduceDown newContext
                    let newLastResult = firstPriorityOption newReduction lastResult
                    go newContext rest newLastResult // (firstOption (tryReduceDown newContext) lastResult)
                | Error e -> (lastResult, Error (context, e))
        let (opt, result) = go emptyExecutionContext (flattenParseStackCommands stackCommands) None
        let partialResult = Option.map (fun (_, ret) -> ret) opt
        let makeDraw r = r.continuations |> List.map (fun c -> c.draw) |> List.fold mergeDraws r.currentContext.draw; 
        Result.cata
            (fun r -> {
                environment = r.currentContext.environment; 
                value = partialResult; 
                draw = makeDraw r;
                error = None;})
            (fun (context, e) -> {
                environment = Map.empty; 
                value = partialResult; 
                draw = makeDraw context;
                error = Some e;})
            result