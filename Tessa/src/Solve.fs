namespace Tessa.Solve

open System
open FSharp.Collections
open Tessa.Language 
open Tessa.Util
open System.Collections.Generic

// todo: Split into more submodules. Solve.Types, Solve.Line (for line solver) etc.?
module Solve =
    module L = Language
    open Util

    type Point = {x: double; y: double;}
    // https://stackoverflow.com/a/26565842/10558918
    [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
    module Point = 
        let x: Point -> double = fun p -> p.x
        let y: Point -> double = fun p -> p.y

    let equalEnough p q = 
        let eps = 0.001
        (abs (p.x - q.x)) < eps && (abs (p.y - q.y)) < eps

    let equalEnoughEps p q eps = 
        (abs (p.x - q.x)) < eps && (abs (p.y - q.y)) < eps

    type Segment = 
        | Straight of Point * Point

    type SegmentChain = Segment list

    type Line = 
        | Vertical of x: double
        | Sloped of xy: Point * m: double

    type Location = double

    // todo: look for calls to okay
    type SolveError = 
        | PointOnEmptySegmentChain of Location * SolveError option
        | LinePerpendicularToSegmentChain of string * SolveError option
        | ExtendSegmentToLine of string * SegmentChain * SolveError option
        | LineVerticalThroughX of Location * SolveError option
        | LineHorizontalThroughY of Location * SolveError option
        | PointLineIntersect of string * Line * Line * SolveError option
        | SegmentPerpendicular of string * SegmentChain * SegmentChain * SolveError option
        | SegmentSnipped of Segment * SolveError option
        | MergeSegmentChains of string * SegmentChain * SegmentChain * SolveError option

    let distance p q =
        sqrt <| (p.x - q.x)**2.0 + (p.y - q.y)** 2.0

    let length segment = 
        // https://gist.github.com/tunght13488/6744e77c242cc7a94859
        // let quadraticBezierLength (orig: Point) (control: Point) (dest: Point) =
        //    let ax = orig.x - 2.0 * control.x + dest.x
        //    let ay = orig.y - 2.0 * control.y + dest.y
        //    let bx = 2.0 * control.x - 2.0 * orig.x
        //    let by = 2.0 * control.y - 2.0 * orig.y
        //    let A = 4.0 * (ax * ax + ay * ay)
        //    let B = 4.0 * (ax * bx + ay * by)
        //    let C = bx * bx + by * by
        //    let Sabc = 2.0 * sqrt(A+B+C)
        //    let A_2 = sqrt(A)
        //    let A_32 = 2.0 * A * A_2
        //    let C_2 = 2.0 * sqrt(C)
        //    let BA = B / A_2

        //    (A_32 * Sabc + A_2 * B * (Sabc - C_2) + (4.0 * C * A - B * B) * log((2.0 * A_2 + BA + Sabc) / (BA + C_2))) / (4.0 * A_32);

        let straightLength orig dest =
            let square x = x * x
            (square (orig.x - dest.x)) + (square (orig.y - dest.y)) |> sqrt

        match segment with
            | Straight (orig, dest) -> straightLength orig dest

    let slope orig dest = 
        match dest.x - orig.x with
            | 0.0 -> None
            | _ -> Some <| (dest.y - orig.y) / (dest.x - orig.x)

    let evaluateLineAt orig dest x =
         slope orig dest 
        |> Option.map (fun m -> m * (x - dest.x) + dest.y)

    let evaluateLineAtWithSlope m point x = 
        m * (x - point.x) + point.y

    let pointOnStraightSegment orig dest location = 
        let dx = dest.x - orig.x
        let dy = dest.y - orig.y
        let newX = orig.x + dx*location
        match evaluateLineAt orig dest newX with
            | None -> {x = orig.x; y = orig.y + dy*location}
            | Some y -> {x = newX; y = y}

    let pointOnSegmentChain (location: Location) (segmentChain: SegmentChain) : Result<Segment * Point, SolveError> = 
        let accumulate (cumulative, m) head =
            let lengthHere = length head
            let newCumulative = lengthHere + cumulative
            (newCumulative, Map.add head (cumulative, newCumulative) m)

        let (_, cumulatives) = List.fold accumulate (0.0, Map.empty) segmentChain
        let total = List.sumBy length segmentChain
        let hitAt = location * total

        let getOnSegment segment orig dest = 
            let (start, finish) = Map.find segment cumulatives
            let locationOnThisSegment = (hitAt - start) / (finish - start)
            (segment, pointOnStraightSegment orig dest locationOnThisSegment)

        let choiceSegment = segmentChain |> List.skipWhile (fun s -> Map.find s cumulatives |> snd |> (>) hitAt) |> List.tryHead

        match choiceSegment with
            | None -> 
                match List.tryLast segmentChain with
                | None -> Error <| PointOnEmptySegmentChain (location, None)
                | Some(Straight(orig, dest) as segment) -> Ok <| getOnSegment segment orig dest
            | Some(Straight(orig, dest) as segment) -> Ok <| getOnSegment segment orig dest

    let solveLinePerpendicular (location: Location) (segmentChain: SegmentChain) = 
        result {
            let! segmentPoint = pointOnSegmentChain location segmentChain
            return 
                match segmentPoint with
                | (Straight(orig, dest), point) -> 
                        match slope orig dest with
                        // Vertical lines become horizontal lines with slope=0
                        | None -> Sloped(point, 0.0)
                        // Horizontal lines become vertical lines
                        | Some 0.0 -> point |> Point.x |> Vertical
                        // All other lines have m -> -1/m
                        | Some m -> Sloped(point, -1.0 / m)
        } |> Result.mapError (fun e -> LinePerpendicularToSegmentChain ("Can't find line perpendicular to empty segment", (Some e)))

    let solveLineExtendSegment segmentChain = 
        match List.length segmentChain with
        | 0 -> Error <| ExtendSegmentToLine("No segments in chain; unable to extend to line", segmentChain, None)
        | 1 ->
            match List.head segmentChain with 
            | Straight(orig, dest) -> 
                match slope orig dest with
                | None -> Ok <| Vertical orig.x
                | Some m -> Ok <| Sloped (orig, m)
        | _ -> Error <| ExtendSegmentToLine("more than 1 segment in chain; unable to extend to line", segmentChain, None)

    let solveLineVerticalThroughX location segmentChain =
        Result.bimap 
            (snd >> Point.x >> Vertical) 
            (fun e -> LineVerticalThroughX (location, Some e))
            (pointOnSegmentChain location segmentChain)

    let solveLineHorizontalThroughY location segmentChain =
        Result.bimap 
            (snd >> fun p -> Sloped(p, 0.0)) 
            (fun e -> LineHorizontalThroughY (location, Some e))
            (pointOnSegmentChain location segmentChain)

    let rotateAround point around direction degree = 
        let degreeAsInt = 
            match degree with
            | L.C2 -> 180
            | L.C3 -> 120
            | L.C4 -> 90
            | L.C6 -> 60

        let degreeAsClockwise = 
            match direction with
            | L.Clockwise -> 360 - degreeAsInt
            | L.CounterClockwise -> degreeAsInt

        let radians = (float degreeAsClockwise) * Math.PI/180.0 

        {x = Math.Cos(radians) * (point.x - around.x) - Math.Sin(radians) * (point.y - around.y) + around.x;
        y = Math.Sin(radians) * (point.x - around.x) + Math.Cos(radians) * (point.y - around.y) + around.y;}
    
    // let solvePointOperated point operation =
    //     match operation with
    //     | L.Rotate(direction, angle, center) -> rotateAround point center direction angle

    let solvePointLineIntersect m n = 
        match (m, n) with
        | (Vertical(_), Vertical(_)) -> Error <| PointLineIntersect("There is no single point at which two vertical lines intersect.", m, n, None)
        | (Sloped(point, slope), Vertical(x)) -> Ok {x = x; y = evaluateLineAtWithSlope slope point x}
        | (Vertical(x), Sloped(point, slope)) -> Ok {x = x; y = evaluateLineAtWithSlope slope point x}
        | (Sloped(point1, slope1), Sloped(point2, slope2)) -> 
            if slope1 = slope2
            then Error <| PointLineIntersect("The lines are either parallel or identical -- no single point of intersection", m, n, None)
            else 
                let x = (slope1 * point1.x - slope2 * point2.x + point2.y - point1.y) / (slope1 - slope2)
                let y = evaluateLineAtWithSlope slope1 point1 x
                Ok {x = x; y = y;}

    let solveSegmentSnipped (original: Segment) (cutAt: SegmentChain) : (Segment * Segment) option = 
        let pointBoundedBy point p q =
            (min p.x q.x) <= point.x && point.x <= (max p.x q.x) && (min p.y q.y) <= point.y && point.y <= (max p.y q.y)

        let pointWithinSegmentBounds point segment = 
            match segment with
            | Straight(orig, dest) -> pointBoundedBy point orig dest

        let segmentsIntersect s1 s2 =  Result.fromOk None <| result {
            let! extend1 = solveLineExtendSegment [s1]
            let! extend2 = solveLineExtendSegment [s2] 
            let! intersect = solvePointLineIntersect extend1 extend2
            return 
                if (pointWithinSegmentBounds intersect s1) && (pointWithinSegmentBounds intersect s2) 
                then Some intersect 
                else None
        } 

        let splitAt segment point =
            match segment with
                | Straight(orig, dest) -> 
                    if equalEnough orig point || equalEnough dest point 
                    then None 
                    else Some (Straight(orig, point), Straight(point, dest))

        let possibleCut = List.map (segmentsIntersect original) cutAt |> List.filter Option.isSome |> List.tryHead |> Option.flatten
        match possibleCut with
            | Some cutPoint -> splitAt original cutPoint 
            | None -> None

    let solveSegmentChainSnipped (original: SegmentChain) (cutAt: SegmentChain) : SegmentChain= 
        let rec search segments = 
            match segments with
            | [] -> []
            | segment::rest -> 
                let possibleSplit = solveSegmentSnipped segment cutAt 
                match possibleSplit with
                    | Some (beforeSplit, afterSplit) -> [beforeSplit]
                    | _ -> segment :: (search rest)
        search original

    let solveSegmentPerpendicular position (origSegment: SegmentChain) (endSegment: SegmentChain) =
        result {
            let! (_, startPoint) = pointOnSegmentChain position origSegment
            let! perpLine = solveLinePerpendicular position origSegment
            let intersectionPoints = 
                endSegment 
                |> List.map (fun x -> Ok [x] |> Result.bind solveLineExtendSegment |> Result.bind (solvePointLineIntersect perpLine))
                |> okays
            return! 
                match intersectionPoints with
                | [] -> Error <| SegmentPerpendicular("Found no intersection between segments", origSegment, endSegment, None)
                | _ -> Ok <| Straight(startPoint, List.minBy (distance startPoint) intersectionPoints)
        }

    let origDest = function
        | Straight(orig, dest) -> (orig, dest)

    let mergeSegmentChains chain1 chain2 =
        let (last1, first2) = (List.last chain1, List.head chain2)
        // This is for when we add arcs

        let (_, last1dest) = origDest last1
        let (first2orig, first2dest) = origDest first2

        if equalEnough last1dest first2orig
        // Rebuild first point with last1dest to account for any minute differences
        then Ok <| chain1 @ (Straight(last1dest, first2dest) :: List.tail chain2)
        else Error <| MergeSegmentChains("The last point of chain1 is not close enough to the first point of chain2", chain1, chain2, None)

    let extendSegmentToPoint chain point =
        let (_, lastPointInChain) = origDest <| List.last chain
        chain @ [Straight(lastPointInChain, point)]

    let prependPointToSegment point chain =
        let (firstPointInChain, _) = origDest <| List.head chain
        Straight(point, firstPointInChain) :: chain

    type SolveContext = 
        {PointContext: Map<L.Point, Result<Point, SolveError>>; 
        SegmentContext: Map<L.Segment, Result<SegmentChain, SolveError>>; 
        LineContext: Map<L.Line, Result<Line, SolveError>>}

    let emptySolveContext = {PointContext = Map.empty; SegmentContext = Map.empty; LineContext = Map.empty;}

    let returnPoint orig solved = 
        state {
            let! _ = State.modify <| fun context -> {context with PointContext = Map.add orig solved context.PointContext}
            return solved
        }

    let returnSegment orig solved = 
        state {
            let! _ = State.modify <| fun context -> {context with SegmentContext = Map.add orig solved context.SegmentContext}
            return solved
        }

    let returnLine orig solved = 
        state {
            let! _ = State.modify <| fun context -> {context with LineContext = Map.add orig solved context.LineContext}
            return solved
        }

    let stateResultBind2 
        (x: State<SolveContext, Result<'a, 'e>>) 
        (y: State<SolveContext, Result<'b, 'e>>) 
        (f: 'a -> 'b -> Result<'c, 'e>) : State<SolveContext, Result<'c, 'e>> =  
        state {
            let! xs = x 
            let! ys = y
            return Result.bind2 xs ys f
        } 

    let stateResultBind x f =  State.map (fun sx -> Result.bind f sx) x

    let rec solvePoint (lpoint: L.Point) : State<SolveContext, Result<Point, SolveError>> = 
        state {
            let! context = State.get 
            let! found =
                match Map.tryFind lpoint context.PointContext with
                    | Some r -> State.result r
                    | None -> 
                        match lpoint with
                        | L.Absolute(x, y) -> State.result <| Ok {x = x; y = y}
                        | L.Operated(origin, op) -> 
                            match op with
                            | L.Rotate(direction, angle, center) -> 
                                stateResultBind2 (solvePoint origin) (solvePoint center) (fun ro rc -> Ok <| rotateAround ro rc direction angle)
                            | L.GlideAround(_) -> failwith "No support yet for GlideAround operation"
                        | L.OnSegment(L.PointOnSegment(position, segment)) -> 
                            state {
                                let! solvedSeg = solveSegment segment
                                return solvedSeg |> Result.bind (pointOnSegmentChain position) |> Result.map snd 
                            }
                        | L.Intersection(line1, line2) -> 
                            stateResultBind2 (solveLine line1) (solveLine line2) solvePointLineIntersect
            return! returnPoint lpoint found
        }

    and solveSegment (segment: L.Segment) : State<SolveContext, Result<SegmentChain, SolveError>> = 
        state {
            let! context = State.get 
            let! found = 
                match Map.tryFind segment context.SegmentContext with
                    | Some chain -> State.result chain
                    | None -> 
                        match segment with
                        | L.Link(p1, p2) -> 
                            stateResultBind2 (solvePoint p1) (solvePoint p2) (fun r1 r2 -> Ok [Straight(r1, r2)])
                        | L.Chain(s, p) -> 
                            stateResultBind2 (solveSegment s) (solvePoint p) (fun rs rp -> Ok <| extendSegmentToPoint rs rp)
                        | L.ReverseChain(p, s) ->
                            stateResultBind2 (solvePoint p) (solveSegment s) (fun rp rs -> Ok <| prependPointToSegment rp rs)
                        | L.Concat(s1, s2) -> 
                            stateResultBind2 (solveSegment s1) (solveSegment s2) mergeSegmentChains
                        | L.Perpendicular(position, originSegment, endSegment) -> 
                            stateResultBind2 (solveSegment originSegment) (solveSegment endSegment) (fun ro re -> Result.map (fun x -> [x]) <| solveSegmentPerpendicular position ro re)
                        | L.Snipped(orig, cutAt) -> 
                            stateResultBind2 (solveSegment orig) (solveSegment cutAt) (fun o c -> Ok <| solveSegmentChainSnipped o c)
            return! returnSegment segment found
        }

    and solveLine (line: L.Line) : State<SolveContext, Result<Line, SolveError>> = 
        state {
            let! context = State.get 
            let! found = 
                match Map.tryFind line context.LineContext with 
                    | Some l -> State.result l 
                    | None ->
                        match line with
                        | L.Line.Perpendicular(pos, segment) -> stateResultBind (solveSegment segment) <| solveLinePerpendicular pos
                        | L.VerticalThroughX(x, segment) -> stateResultBind (solveSegment segment) <| solveLineVerticalThroughX x 
                        | L.HorizontalThroughY(y, segment) -> stateResultBind (solveSegment segment) <| solveLineHorizontalThroughY y 
                        | L.ExtendSegment(segment) -> stateResultBind (solveSegment segment) <| solveLineExtendSegment 
            return! returnLine line found
        }

    type PointSolver = L.Point -> Result<Point, SolveError>
    type SegmentSolver = L.Segment -> Result<SegmentChain, SolveError>
    type LineSolver = L.Line -> Result<Line, SolveError>

    type Solver = {
        line: LineSolver;
        segment: SegmentSolver;
        point: PointSolver;
    }

    let makeSolvers () : Solver =
        let mutable initContext = emptySolveContext

        let solvePoint p = 
            let (solved, newContext) = State.run (solvePoint p) initContext
            initContext <- newContext
            solved

        let solveSegment s =
            let (solved, newContext) = State.run (solveSegment s) initContext
            initContext <- newContext
            solved

        let solveLine l =
            let (solved, newContext) = State.run (solveLine l) initContext
            initContext <- newContext
            solved

        {line = solveLine; segment = solveSegment; point = solvePoint;}

    //
    // POLYGONS
    //


    type PointId = PointId of int
    type SegmentId = SegmentId of PointId * PointId 

    // canonicalize points phase, eps = 0.001 maybe
    // this changes input to View

    type Polygon = {
        segments: SegmentId list;
    }

    type CanonicizerState = {
        epsilon: float;
        idToPoint: Map<PointId, Point>;
        nextId: int;
    }

    let pointIdToPoint cstate pointId =
        // it's a bit sketkchy using find instead of tryFind. We'll get an exception
        // if there is no such id. But as long as we're getting ids by the state's provisioning we'll
        // be fine.
        Map.find pointId cstate.idToPoint 

    let segmentIdToSegment cstate (SegmentId(p, q)) = 
        Straight(pointIdToPoint cstate p, pointIdToPoint cstate q)

    let pointToPointId point = 
        state {
            // could optimize by having an incomplete pointToPointId map -- incomplete because
            // it wouldn't account for all possible epsilon-allowed variations. 
            // but we're not going from raw to id nearly as often as id to raw, so I don't think
            // it's worth the extra complexity for now.
            let! s = State.get
            let existing = Map.toList s.idToPoint |> List.filter (fun (id, q) -> equalEnoughEps point q s.epsilon) |> List.tryHead
            match existing with 
            | Some (pointId, _) -> return pointId
            | None -> 
                let newId = PointId s.nextId
                do! State.put {s with nextId = s.nextId + 1; idToPoint = Map.add newId point s.idToPoint}
                return newId
        }
    
    let segmentToSegmentId (Straight(orig, dest)) = 
        state {
            let! orig' = pointToPointId orig 
            let! dest' = pointToPointId dest 
            return SegmentId (orig', dest')
        }

    // let canonicalize points = 
    //     List.allPairs // map from original to canon
    let atomizeSegment segment chain = 
        let rec splits atoms = 
            match atoms with 
            | [] -> Set.empty
            | atom :: xs -> 
                let nextSplits = List.map (fun s -> solveSegmentSnipped atom [s]) chain |> somes |> List.unpack |> Set.ofList
                if not (Set.isEmpty nextSplits) then Set.union nextSplits (splits xs) else Set.add atom (splits xs)

        let rec go atoms = 
            let afterSplit = splits (List.ofSeq atoms)
            if Set.count afterSplit = Set.count atoms 
            then afterSplit
            else go afterSplit

        Set.ofList [segment] |> go |> Set.toList

    let atomizeSegments segments =
        let emptyCanonState = {epsilon = 0.001; nextId = 0; idToPoint = Map.empty;}
        let atomized = List.collect (fun s -> atomizeSegment s segments) segments 
        let canonicized = State.sequence <| List.map segmentToSegmentId atomized
        State.run canonicized emptyCanonState

    let closed (pointSet: Set<Set<PointId>>) = 
        let rec asTuples pointLinks visitedBegin = 
            match pointLinks with
            | [] -> []
            | [p; q] :: rest -> 
                if Set.contains p visitedBegin
                then (q, p) :: (asTuples rest <| Set.add q visitedBegin)
                else (p, q) :: (asTuples rest <| Set.add p visitedBegin)
            | _ -> failwith "should be impossible"
        let tupled = asTuples (Set.toList (Set.map Set.toList pointSet)) Set.empty
        let (beginnings, endings) = List.unzip tupled
        let closedPath = Set.ofList beginnings = Set.ofList endings
        if closedPath then Some <| List.map SegmentId tupled else None


    // join must work when only some segments form completed polygons and must allow other segments to continue existing
    let joinToPolygons (segments : SegmentId list) = 

        let polygonIsSuperset (p1: Set<Set<PointId>>) (p2: Set<Set<PointId>>) = 
            // it's point based, not segment based
            let allPointsP1 = p1 |> Set.toList |> List.collect Set.toList |> Set.ofList
            let allPointsP2 = p2 |> Set.toList |> List.collect Set.toList |> Set.ofList
            Set.isSuperset allPointsP1 allPointsP2

        // It is 2020 after all...
        let rec go (points: PointId list) (visitedPoints: Set<PointId>) (candidates: Set<Set<PointId>> list) (elected: Set<Set<PointId>> list) = 
            match points with 
            | [] -> elected 
            | p :: ps -> 
                let hasSegmentUsingPoint candidate = Set.exists (fun pointSet -> Set.contains p pointSet) candidate
                let (segmentsInCandidatesUsingPoint, unelectableCandidates) = List.partition hasSegmentUsingPoint candidates
                let augmented = 
                    List.cartesianProduct segmentsInCandidatesUsingPoint segmentsInCandidatesUsingPoint
                    |> List.collect (fun (x, y) -> [Set.union x y; x; y])
                    |> List.append unelectableCandidates
                    |> List.distinct

                // failAndPrint (p, segmentsInCandidatesUsingPoint, unelectableCandidates)

                // failAndPrint augmented

                let augmentedWithoutSupersets = List.filter (fun aug -> not <| List.exists (fun poly -> polygonIsSuperset aug poly) elected) augmented

                let (newPolygons, newCandidates) = List.partition (Option.isSome << closed) augmentedWithoutSupersets
                let prunedExistingPolygons = List.filter (fun poly -> not <| List.exists (fun newPoly -> polygonIsSuperset poly newPoly) newPolygons) elected
                // failAndPrint newCandidates

                go ps (Set.add p visitedPoints) newCandidates (newPolygons @ prunedExistingPolygons)

        let allPoints = List.collect (fun (SegmentId(p, q)) -> [p; q]) segments |> List.distinct

        let initialCandidates = List.map (fun (SegmentId(p, q)) -> Set.ofList [Set.ofList [p; q]]) segments

        go allPoints Set.empty initialCandidates []



