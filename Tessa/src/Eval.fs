namespace Tessa.Eval

open Tessa.Language
open Tessa.Solve
open Tessa.Parse
open Tessa.Util
open FSharpPlus

// todo: make sure Parse and Solve both return Result
module Eval = 
    module P = Parse
    module L = Language
    module S = Solve
    open Util

    type PrimitiveProcedure = 
        | AddNumber
        | Assign
        | RecordBuilder 
        | RecordAccess
        | ArrayBuilder 
        | Lambda

        | LinkPoints 
        | Perpendicular 
        | Intersect 
        | At 
        | ApplyOp 
        | Snip
        | Draw
        // has optional assignment semantics also! more convenient.
        | CellBuild

    // todo: Need to pipe Lex pos into Parse so I can add positions here
    type EvalError =
        | UndefinedVariable of var: string * message: string
        | AddingNonNumbers of Exp list
        | ApplyingNonFunction of Exp
        | UnbalancedParenExtraClose
        | AssignError
        | RecordBuildingError
        | RecordAccessError of field: string * record: Exp option

    and Exp =
        | Number of float
        | Identifier of string
        | PrimitiveProcedure of PrimitiveProcedure
        | Quote of P.StackCommand
        | Record of Map<string, Exp>
        // | Lambda
        // | LanguageExp of LanguageExp
        // Language Unsolved

    and LanguageExp = 
        | LPoint of L.Point
        | LSegment of L.Segment
        | LLine of L.Line
        | LOperation of L.Operation
        | LPolygon of L.Polygon

    and SolveExp = 
        | SPoint of S.Point
        | SSegment of S.Segment
        | SLine of S.Line

    let toNumber exp = 
        match exp with 
        | Number n -> Ok n 
        | other -> Error other 

    type Operation =
        | Primitive of PrimitiveProcedure
        // | Fun

    type OperationState = 
        | Empty 
        | EmptyAcceptNext
        | Op of Operation

    type StackExecutionContext = {
        currentOp: OperationState;
        arguments: Exp list;
        environment: Map<string, Exp>;
        ret: Exp option;
        // doesn't work because of primitive functions
        // subExpressions: Map<Exp, Exp>;
        // continuation implicitly stored in list
    }

    type ExecutionContext = {
        // stackContext[i] has continuation stackContext[i + 1]
        continuations: StackExecutionContext list;
        currentContext: StackExecutionContext
        solveContext: S.SolveContext;
        // have a Solve function to seemlessly evaluate 
    }

    type Environment = Map<string, Exp>
    type PrimitiveProcedureFn = (Exp list) -> Result<Exp * Environment, EvalError>

    let parsePrimitiveToEvalPrimitive = function
        | P.Assign -> Assign
        | P.ArrayBuilder -> ArrayBuilder
        | P.RecordBuilder -> RecordBuilder
        | P.LinkPoints -> LinkPoints
        | P.Perpendicular -> Perpendicular
        | P.Intersect -> Intersect
        | P.At -> At
        | P.ApplyOp -> ApplyOp
        | P.Snip -> Snip
        | P.RecordAccess -> RecordAccess
        | P.Draw -> Draw
        | P.Lambda -> Lambda
        | P.CellBuild -> CellBuild

    let addNumber arguments env =
        let numbers = List.map toNumber arguments
        let errs = errors numbers
        let oks = okays numbers

        if not (List.isEmpty errs) 
        then Error <| AddingNonNumbers errs 
        else Ok(List.sum oks |> Number, env) 

    let assign arguments env = 
        match arguments with 
        | Quote(P.Expression(P.Identifier i)) :: [a] -> Ok(a, Map.add i a env)
        | _ -> Error AssignError // todo: could make this a lot more specific

    let makeRecord arguments env =
        let lookupThenTupTo lookupSym = 
            match Map.tryFind lookupSym env with
            | None -> Error <| UndefinedVariable(lookupSym, "Trying to make a record with field " 
                + lookupSym + "; no value specified, and failed to lookup symbol in environment.")
            | Some exp -> Ok (lookupSym, exp)

        let rec partition args = 
            let recurseIfOk (rest: Exp list) (resultVal: Result<string * Exp, EvalError>) = monad {
                let! trueResult = resultVal 
                let! restResult = partition rest 
                return trueResult :: restResult
            }
            match args with 
            | [] -> Ok []
            | [Quote(P.Expression(P.Identifier i1))] -> lookupThenTupTo i1 |> recurseIfOk []
            | Quote(P.Expression(P.Identifier i1)) :: (Quote(P.Expression(P.Identifier i2)) as q2) :: rest -> 
                lookupThenTupTo i1 |> recurseIfOk (q2 :: rest)
            | Quote(P.Expression(P.Identifier i1)) :: x :: rest -> Ok (i1, x) |> recurseIfOk rest
            | _ -> Error RecordBuildingError
        
        let recordMap = Result.map listToMap <| partition arguments
        Result.map (fun record -> (Record record, env)) recordMap

    let recordAccess arguments env = 
        match arguments with
        | [(Record r); (Quote(P.Expression(P.Identifier i)))] -> 
            match Map.tryFind i r with 
            | None -> Error <| RecordAccessError(i, Some <| Record r)
            | Some v -> Ok (v, env)
        | _ -> Error <| RecordAccessError("There aren't two arguments, or they aren't records and symbols, or I don't know -- you messed up.", None)


    let lookupPrimitiveProcedure = function
        | AddNumber -> addNumber
        | Assign -> assign
        | RecordBuilder -> makeRecord
        | RecordAccess -> recordAccess

    let startingEnvironment = 
        Map.empty 
        |> Map.add "plus" (PrimitiveProcedure AddNumber)

    let emptyStackExecutionContext = {
        currentOp = Empty;
        arguments = [];
        environment = startingEnvironment;
        ret = None;}
        //subExpressions = Map.empty}

    let liftToExecutionContext 
        (f: StackExecutionContext -> Result<StackExecutionContext, EvalError>) 
        : ExecutionContext -> Result<ExecutionContext, EvalError>  = fun stackContext ->
            monad {
                let! newContext = f stackContext.currentContext
                return {stackContext with currentContext = newContext}
            }

    let acceptExpression exp context = 
        match context.currentOp with
            | Empty -> Ok <| {context with arguments = exp :: context.arguments}
            | EmptyAcceptNext -> 
                match exp with 
                | PrimitiveProcedure p -> Ok <| {context with currentOp = Op (Primitive p)}
                | _ -> Error <| ApplyingNonFunction exp
            | Op _ -> Ok <| {context with arguments = exp :: context.arguments}

    let acceptNextOp context = Ok <| {context with currentOp = EmptyAcceptNext}

    let reduceStack context = 
        match context.currentOp with 
            | Empty -> 
                let ret = List.tryHead context.arguments
                Ok {context with ret = ret;} // currentOp = Empty;} // arguments = [];}
            | EmptyAcceptNext -> Ok {context with currentOp = Empty; arguments = []; ret = List.tryHead context.arguments;}
            | Op o -> 
                match o with 
                | Primitive p -> monad {
                    let fn = lookupPrimitiveProcedure p
                    let! (applied, newEnv) = fn (List.rev context.arguments) context.environment
                    let newStack = {context with arguments = [applied]; currentOp = Empty; environment = newEnv; ret = Some applied;}
                    return newStack
                }

    let emptyExecutionContext = {
        continuations = [];
        currentContext = emptyStackExecutionContext;
        solveContext = S.emptySolveContext;
    }

    // let applyPrimitive context (primitiveProcedure: PrimitiveProcedureFn) = monad {
    //     let! newStackExecutionContext = primitiveProcedure context.currentContext.beforeOp context.currentContext.afterOp
    //     {context with currentContext = newStackExecutionContext}
    // }

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

    let evalStackCommand initContext stackCommand = 
        match stackCommand with 
        | BeginNewStack -> 
            let current = initContext.currentContext
            let top = {emptyStackExecutionContext with environment = current.environment}
            Ok <| {initContext with currentContext = top; continuations = current :: initContext.continuations;}
        | Expression word -> evalWord initContext word >>= (fun e -> (liftToExecutionContext (acceptExpression e) initContext))
        // continuation pushing . We have a return, now we just need to push it to the before op before us
        | EndStack -> 
            monad {
                let! newStackContext = reduceStack initContext.currentContext
                return {initContext with currentContext = {newStackContext with arguments = []; currentOp = Empty;}}
            }
        | ReduceAndPushOp(maybePrimitive) -> 
            // todo: reduce is possible
            match maybePrimitive with
            | Some primitive -> 
                monad {
                    let! acceptingContext = liftToExecutionContext (reduceStack >=> acceptNextOp) initContext
                    let evalPrimitive = parsePrimitiveToEvalPrimitive primitive |> PrimitiveProcedure
                    return! (liftToExecutionContext (acceptExpression evalPrimitive) acceptingContext)
                }
            | None -> liftToExecutionContext (reduceStack >=> acceptNextOp) initContext
        | ReturnNewStack -> 
            monad {
                let! newStackContext = reduceStack initContext.currentContext
                let ret = newStackContext.ret
                let returnTo = List.tryHead initContext.continuations 
                // failAndPrint (newStackContext, ret, returnTo)
                let newContext = 
                    match returnTo with
                    | None -> Error UnbalancedParenExtraClose 
                    | Some continuation -> 
                        let updatedCurrentContext =  {continuation with arguments = tryCons ret continuation.arguments}
                        let newContext = {initContext with continuations = List.tail initContext.continuations; currentContext = updatedCurrentContext}
                        Ok newContext
                return! newContext
            }

    let eval stackCommands =
        let rec go context commands = 
            match commands with 
            | [] -> Ok context
            | command :: rest -> monad {
                let! newContext = evalStackCommand context command
                return! go newContext rest
            }
        go emptyExecutionContext (flattenParseStackCommands stackCommands)

            


    // and StackCommand =
    //     | ReduceAndPushOp of PrimitiveProcedure option
    //     | BeginNewStack
    //     | EndStack
    //     | Expression of Word



    // let primitiveLookup : Map<string, (Exp list -> Exp)> =
    //     [("add", )]
