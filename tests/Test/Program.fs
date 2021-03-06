﻿// Learn more about F# at http://fsharp.org

open System
open DiffSharp
open DiffSharp.Model
open DiffSharp.Optim
open DiffSharp.Data


[<EntryPoint>]
let main _ =
    printfn "Hello World from F#!"

    dsharp.config(backend=Backend.Torch)
    dsharp.seed(12)

    let dataset = MNIST("./data", train=true)
    let dataloader = dataset.loader(64, shuffle=true)

    let cnn() =
        let convs = Conv2d(1, 32, 3)
                    --> dsharp.relu
                    --> Conv2d(32, 64, 3)
                    --> dsharp.relu
                    --> dsharp.maxpool2d 2
        let k = dsharp.randn([1;1;28;28]) --> convs --> dsharp.nelement
        convs
        --> dsharp.flatten 1
        --> Linear(k, 128)
        --> dsharp.relu
        --> Linear(128, 10)

    let feedforward() =
        dsharp.flatten 1
        --> Linear(28*28, 128)
        --> Linear(128, 10)

    let net = cnn()
    // let net = feedforward()
    printfn "net params: %A" net.nparameters

    printfn "%A" net.parameters.backend
    Optimizer.adam(net, dataloader, dsharp.crossEntropyLoss, iters=200, threshold=0.1)
    let i, t = dataset.item(0)
    let o = i --> dsharp.move() --> dsharp.unsqueeze(0) --> net
    printfn "%A %A %A" o o.backend t


    0 // return an integer exit code
