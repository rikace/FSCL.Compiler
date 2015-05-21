﻿namespace FSCL.Compiler.FunctionTransformation

open FSCL.Compiler
open System.Collections.Generic
open System.Reflection
open System
open Microsoft.FSharp.Quotations

[<StepProcessor("FSCL_CONDITIONAL_ASSIGN_TRANSFORMATION_PROCESSOR", "FSCL_FUNCTION_TRANSFORMATION_STEP",
                Dependencies = [| "FSCL_DYNAMIC_ALLOCATION_LIFTING_TRANSFORMATION_PROCESSOR" |])>]
type ConditionalAssignmentTransformation() =   
    inherit FunctionTransformationProcessor()
    
    let rec ReplaceReturnExpressionWithVarSet (expr: Expr, v: Var, engine:FunctionTransformationStep) =
        match expr with
        | Patterns.Let(v, value, body) ->                
            Expr.Let(v, value, ReplaceReturnExpressionWithVarSet(body, v, engine))
        | Patterns.Sequential(e1, e2) ->
            Expr.Sequential(e1, ReplaceReturnExpressionWithVarSet(e2, v, engine))                
        | Patterns.IfThenElse(cond, ifexp, elsexp) ->
            Expr.IfThenElse(cond, 
                ReplaceReturnExpressionWithVarSet(ifexp, v, engine),
                ReplaceReturnExpressionWithVarSet(elsexp, v, engine))  
        | ExprShape.ShapeLambda(v, e) ->
            Expr.Lambda(v, 
                ReplaceReturnExpressionWithVarSet(e, v, engine))              
        | _ ->
            if expr.Type = v.Type then
                Expr.VarSet(v, expr)
            else
                failwith "Cannot find return expression in a nested let"

    let rec MoveAssignmentIntoBody(var:Var, expr, engine:FunctionTransformationStep) =
        match expr with
        | Patterns.Sequential (e1, e2) ->
            Expr.Sequential(e1, MoveAssignmentIntoBody (var, e2, engine))
        | Patterns.IfThenElse(condinner, ifbinner, elsebinner) ->
            Expr.IfThenElse(condinner, MoveAssignmentIntoBody(var, ifbinner, engine), MoveAssignmentIntoBody(var, elsebinner, engine))     
        | Patterns.Let (e, v, body) ->
            Expr.Let(e, v, MoveAssignmentIntoBody(var, body, engine))
        | Patterns.Var (v) ->
            Expr.VarSet(var, Expr.Var(v))
        | Patterns.Value (v) ->
            Expr.VarSet(var, Expr.Value(v))
        | Patterns.Call (e, i, a) ->
            if e.IsSome then
                Expr.VarSet(var, Expr.Call(e.Value, i, a))
            else
                Expr.VarSet(var, Expr.Call(i, a))
        | _ ->
            raise (CompilerException("Cannot determine variable assignment in if-then-else construct. Try to transform v = if .. else ..; into v; if .. v <- .. else .. v <- .."))

    let rec MoveArraySetIntoBody(o:Expr option, mi:MethodInfo, a:Expr list, substituteIndex:int, expr, engine:FunctionTransformationStep) =
        match expr with
        | Patterns.Sequential (e1, e2) ->
            Expr.Sequential(e1, MoveArraySetIntoBody (o, mi, a, substituteIndex, e2, engine))
        | Patterns.IfThenElse(condinner, ifbinner, elsebinner) ->
            Expr.IfThenElse(condinner, MoveArraySetIntoBody(o, mi, a, substituteIndex, ifbinner, engine), MoveArraySetIntoBody(o, mi, a, substituteIndex, elsebinner, engine))     
        | Patterns.Let (e, v, body) ->
            Expr.Let(e, v, MoveArraySetIntoBody(o, mi, a, substituteIndex, body, engine))
        | Patterns.Var (v) ->
            Expr.Call(mi, List.mapi(fun i el -> if i = substituteIndex then Expr.Var(v) else el) a)
        | Patterns.Value (v, t) ->
            Expr.Call(mi, List.mapi(fun i el -> 
                if i = substituteIndex then 
                    Expr.Value(v, t)
                else el) a)
        | Patterns.Call (subo, subi, suba) ->
            if subo.IsSome then
                Expr.Call(mi, List.mapi(fun i el -> if i = substituteIndex then Expr.Call(subo.Value, subi, suba) else el) a)
            else
                Expr.Call(mi, List.mapi(fun i el -> if i = substituteIndex then Expr.Call(subi, suba) else el) a)
        | _ ->
            raise (CompilerException("Cannot determine variable assignment in if-then-else construct. Try to transform v = if .. else ..; into v; if .. v <- .. else .. v <- .."))
                                                 
    override this.Run((expr, cont, def), en, opts) =
        let engine = en :?> FunctionTransformationStep
        match expr with
        // Check let-let
        | Patterns.Let(v, e, body) ->
            match e with
            | Patterns.Let(v2, e2, body2) ->                 
                let norm = Expr.Let(v, Expr.DefaultValue(v.Type), 
                                Expr.Sequential(Expr.Let(v2, e2, ReplaceReturnExpressionWithVarSet(body2, v, engine)),
                                                body))
                cont(norm)
            | _ ->
                def(expr)
        // Check branches
        | Patterns.Let(v, e, body) ->
            match e with
            | Patterns.IfThenElse(cond, ib, eb) ->                    
                let fixedExpr = MoveAssignmentIntoBody(v, e, engine)
                Expr.Sequential(
                    Expr.Let(v, e, Expr.Value(0)),
                    cont(fixedExpr))
            | _ ->
                def(expr)
        | Patterns.VarSet (v, e) ->
            match e with
            | Patterns.IfThenElse(cond, ib, eb) ->                    
                let fixedExpr = MoveAssignmentIntoBody(v, e, engine)
                cont(fixedExpr)
            | _ ->
                def(expr)                 
        | Patterns.Call (e, mi, a) ->
            if mi.DeclaringType <> null then
                if mi.DeclaringType.Name = "IntrinsicFunctions" then                    
                    if mi.Name.StartsWith "SetArray" then
                        let substituteIndex = a.Length - 1

                        match a.[substituteIndex] with
                        | Patterns.IfThenElse(cond, ib, eb) ->                    
                            let fixedExpr = MoveArraySetIntoBody(e, mi, a, substituteIndex, a.[substituteIndex], engine)
                            cont(fixedExpr)
                        | _ ->
                            def(expr)
                    else
                        def(expr)
                else
                    def(expr)
            else
                def(expr)
        | _ ->
            def(expr)                   