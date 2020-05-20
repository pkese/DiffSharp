namespace DiffSharp.Model
open DiffSharp
open DiffSharp.Util
open System.Collections.Generic

type Parameter =
    val mutable value:Tensor
    new(value) = {value=value}
    member p.forwardDiff(derivative:Tensor, ?tag:uint32) = p.value <- p.value.forwardDiff(derivative, ?tag=tag)
    member p.reverseDiff(?tag:uint32) = p.value <- p.value.reverseDiff(?tag=tag)
    member p.noDiff() = p.value <- p.value.noDiff()
    override p.ToString() = sprintf "Parameter(shape: %A, value: %A)" p.value.shape p.value

type ParameterDict() =
    member val values:Dictionary<string, Parameter> = Dictionary()
    member d.Item
        with get key = d.values.[key].value
        and set key v = d.values.[key].value <- v
    member d.add(name, parameter) = d.values.Add(name, parameter)
    member d.add(parameters:list<string*Parameter>) = for (n, p) in parameters do d.add(n, p)
    member d.add(parameters:ParameterDict) = for KeyValue(n, p) in parameters.values do d.add(n, p)
    member d.copy() = d.map(fun (t:Tensor) -> t)
    member d.map(f:string*Parameter->string*Parameter) =
        let ret = ParameterDict()
        for KeyValue(n, p) in d.values do ret.values.Add(f(n,p))
        ret
    member d.map(f:string*Tensor->string*Tensor) = d.map(fun (n, p:Parameter) -> let nn, tt = f(n, p.value) in nn, Parameter(tt))
    member d.map(f:Tensor->Tensor) = d.map(fun (n,t) -> n, f t)
    member d.set(parameters:ParameterDict) = d.iter(fun (n, p) -> p.value <- parameters.[n])
    member d.iter(f:string*Parameter->unit) = for KeyValue(n, p) in d.values do f(n,p)
    member d.forwarddiff(derivatives:ParameterDict, ?tag:uint32) = 
        let tag = defaultArg tag GlobalNestingLevel.Current
        d.iter(fun (n, p) -> p.forwardDiff(derivatives.[n], tag))
    member d.reverseDiff(?tag:uint32) = 
        let tag = defaultArg tag GlobalNestingLevel.Current
        d.iter(fun (_, p) -> p.reverseDiff(tag))
    member d.noDiff() = d.iter(fun (_, p) -> p.noDiff())
    member d.primal with get() = d.map(fun (t:Tensor)->t.primal)
    member d.derivative with get() = d.map(fun (t:Tensor)->t.derivative)
    member d.nelement with get() = [|for t in d.values.Values do t.value.nelement|] |> Array.sum
    member d.flatten() =
        let ts = [for t in d.values.Values do t.value.view(-1)]
        dsharp.cat(ts)
    member d.unflatten(tensors:Tensor) =
        let shapes = [|for t in d.values.Values do t.value.shape|]
        let sizes = [|for s in shapes do shapeLength s|]
        let ts = Array.map2 (fun (t:Tensor) (s:int[]) -> t.view(s)) (tensors.split(sizes)) shapes
        let mutable i = 0
        let keys = getKeys d.values
        for n in keys do
            d.[n] <- ts.[i]
            i <- i+1
    member d.unflattenToNew(tensors:Tensor) = 
        let dd = d.copy()
        dd.unflatten(tensors)
        dd
    override d.ToString() =
        let sb = System.Text.StringBuilder()
        for KeyValue(n, p) in d.values do sb.AppendLine(sprintf "%A, %A" n p) |> ignore
        sb.ToString()
        

[<AbstractClass>]
type Model() =
    member val Parameters:ParameterDict = ParameterDict()
    member val SubModels:Dictionary<string, Model> = Dictionary()
    member m.add(parameters:seq<obj>, ?names:seq<string>) =
        let parameters = parameters |> Seq.toArray
        let names = defaultArg names (Seq.init (parameters.Length) (fun i -> sprintf "p__%d" i)) |> Seq.toArray
        if parameters.Length <> names.Length then failwithf "Expecting parameters.Length (%A) and names.Length (%A) to be same" parameters.Length names.Length
        for p, n in Array.zip parameters names do
            match (box p) with
            | :? Parameter as p -> 
                m.Parameters.add(n, p)
            | :? Model as mm ->
                m.SubModels.Add(n, mm)
                m.Parameters.add(mm.Parameters.map(fun (nn, pp:Parameter) -> (n + "__" + nn, pp)))
            | _ -> failwithf "Unsupported type. Expecting a Parameter or Model"
    member m.forwardDiff(derivatives:ParameterDict) = m.Parameters.forwarddiff(derivatives)
    member m.reverseDiff() = m.Parameters.reverseDiff()
    member m.noDiff() = m.Parameters.noDiff()
    member m.setParameters(parameters:ParameterDict) = m.Parameters.set(parameters)
    member m.setParameters(parameters:Tensor) = m.Parameters.unflatten(parameters)
    member m.getParameters() = m.Parameters.flatten()
    member m.nparameters = m.Parameters.nelement
    abstract member forward: Tensor -> Tensor
    member m.forwardParams (input:Tensor) (parameters:Tensor) =
        m.setParameters(parameters)
        let f = m.forward(input) in m.noDiff(); f
    member m.forwardCompose (f:Tensor->Tensor) (input:Tensor) (parameters:Tensor) =
        m.forwardParams input parameters |> f
    member m.forwardLoss (f:Tensor->Tensor->Tensor) (input:Tensor) (target:Tensor) (parameters:Tensor) =
        m.forwardCompose (f target) input parameters
    static member create ps f =
        let model = { new Model() with override __.forward(x) = f x}
        model.add(ps)
        model
    static member compose (model1:Model) (model2:Model) =
        Model.create [model1; model2] (model1.forward >> model2.forward)


type Weight() =
    static member kaiming(fanIn, fanOut, ?a:float) = 
        // He et al. 2015. https://arxiv.org/abs/1502.01852
        let a = defaultArg a (sqrt 5.)
        let w = dsharp.randn([fanIn; fanOut])
        let s = sqrt (2. / ((1. + a*a) * (float fanIn)))
        w * s
    static member standard(shape:int[], k:float) =
        -k + dsharp.rand(shape) * 2*k


type Linear(inFeatures, outFeatures, ?bias:bool) =
    inherit Model()
    let bias = defaultArg bias true
    let w = Parameter(Weight.kaiming(inFeatures, outFeatures))
    let k = 1./sqrt (float outFeatures)
    let b = Parameter(if bias then Weight.standard([|outFeatures|], k) else dsharp.zero())
    do base.add([w;b],["Linear__weight";"Linear__bias"])
    override l.forward(value) =
        let f = dsharp.matmul(value, w.value)
        if bias then f + b.value else f


type Conv1d(inChannels:int, outChannels:int, kernelSize:int, ?stride:int, ?padding:int, ?dilation:int, ?bias:bool) =
    inherit Model()
    let bias = defaultArg bias true
    let k = 1./ sqrt (float (inChannels*kernelSize))
    let w = Parameter <| Weight.standard([|outChannels; inChannels; kernelSize|], k)
    let b = Parameter <| if bias then Weight.standard([|outChannels|], k) else dsharp.zero()
    do base.add([w;b],["Conv1d__weight";"Conv1d__bias"])
    override c.forward(value) =
        let f = dsharp.conv1d(value, w.value, ?stride=stride, ?padding=padding, ?dilation=dilation)
        if bias then f + b.value.expand([value.shape.[0]; outChannels]).view([value.shape.[0]; outChannels; 1]) else f


type Conv2d(inChannels:int, outChannels:int, ?kernelSize:int, ?stride:int, ?padding:int, ?dilation:int, ?kernelSizes:seq<int>, ?strides:seq<int>, ?paddings:seq<int>, ?dilations:seq<int>, ?bias:bool) =
    inherit Model()
    let kernelSizes = 
        match kernelSize, kernelSizes with
        | Some _ , Some _ -> failwithf "Expecting only one of kernelSize, kernelSizes"
        | Some k, None -> [|k; k|]
        | None, Some k -> let k = k |> Array.ofSeq in if k.Length <> 2 then failwithf "Expecting kernelSizes to have length two" else k
        | _ -> [|1; 1|]
    let bias = defaultArg bias true
    let k = 1./ sqrt (float (inChannels*kernelSizes.[0]*kernelSizes.[1]))
    let w = Parameter <| Weight.standard([|outChannels; inChannels; kernelSizes.[0]; kernelSizes.[1]|], k)
    let b = Parameter <| if bias then Weight.standard([|outChannels|], k) else dsharp.zero()
    do base.add([w;b],["Conv2d__weight";"Conv2d__bias"])
    override c.forward(value) =
        let f = dsharp.conv2d(value, w.value, ?stride=stride, ?strides=strides, ?padding=padding, ?paddings=paddings, ?dilation=dilation, ?dilations=dilations)
        if bias then f + b.value.expand([value.shape.[0]; outChannels]).view([value.shape.[0]; outChannels; 1; 1]) else f


type Conv3d(inChannels:int, outChannels:int, ?kernelSize:int, ?stride:int, ?padding:int, ?dilation:int, ?kernelSizes:seq<int>, ?strides:seq<int>, ?paddings:seq<int>, ?dilations:seq<int>, ?bias:bool) =
    inherit Model()
    let kernelSizes = 
        match kernelSize, kernelSizes with
        | Some _ , Some _ -> failwithf "Expecting only one of kernelSize, kernelSizes"
        | Some k, None -> [|k; k; k|]
        | None, Some k -> let k = k |> Array.ofSeq in if k.Length <> 3 then failwithf "Expecting kernelSizes to have length three" else k
        | _ -> [|1; 1; 1|]
    let bias = defaultArg bias true
    let k = 1./ sqrt (float (inChannels*kernelSizes.[0]*kernelSizes.[1]*kernelSizes.[2]))
    let w = Parameter <| Weight.standard([|outChannels; inChannels; kernelSizes.[0]; kernelSizes.[1]; kernelSizes.[2]|], k)
    let b = Parameter <| if bias then Weight.standard([|outChannels|], k) else dsharp.zero()
    do base.add([w;b],["Conv3d__weight";"Conv3d__bias"])
    override c.forward(value) =
        let f = dsharp.conv3d(value, w.value, ?stride=stride, ?strides=strides, ?padding=padding, ?paddings=paddings, ?dilation=dilation, ?dilations=dilations)
        if bias then f + b.value.expand([value.shape.[0]; outChannels]).view([value.shape.[0]; outChannels; 1; 1; 1]) else f