﻿module FSec.Tests

open System
open System.IO
open System.Text.RegularExpressions
open System.Threading
open System.Xml
open System.Xml.Schema

open FsCheck
open ICSharpCode.SharpZipLib.Zip
open Swensen.Unquote
open Expecto

open FSecurity
open System.Text
open System.Net

[<Tests>]
let injection_tests =
  testList "injection tests" [
    testProperty "XPath input injection" <| fun () ->
      FSec.xpathInject
      |> Gen.two
      |> Gen.zip (Gen.elements 
          [ Vulnerable, XPath.vulnerable
            Prevented, XPath.prevented ])
      |> Arb.fromGen
      |> Prop.forAll <| fun ((exp, sut), (user, pass)) -> 
          exp =! sut user pass
    testProperty "XSS input injection" <| fun () ->
      FSec.xssInject
      |> Gen.zip (Gen.elements 
          [ true, HTML.vulnerable
            false, HTML.prevented ])
      |> Arb.fromGen
      |> Prop.forAll <| fun ((exp, sut), x) ->
          sut x |> Option.exists (fun y -> y.Contains x)
                |> (=!) exp

    testPropertyWithConfig ({ FsCheckConfig.defaultConfig with maxTest = 1 }) "XML bomb injection timeout" <| fun () ->
      FSec.xmlBomb
      |> Arb.fromGen
      |> Prop.forAll <| fun x ->
          let ss = XmlReaderSettings () in
              ss.DtdProcessing <- DtdProcessing.Parse;
              ss.ValidationFlags <- XmlSchemaValidationFlags.None;
              ss.ValidationType <- ValidationType.None;
              ss.Async <- true
          use r = XmlReader.Create (new StringReader (x), ss)
          use cts = new CancellationTokenSource () in
              cts.CancelAfter 50

          try let a = r.ReadAsync () |> Async.AwaitTask
              while Async.RunSynchronously (a, cancellationToken=cts.Token) do ()
              false
          with | :? OperationCanceledException -> true
    
    testProperty "XML malicious generation" <| fun () ->
      FSec.xmlMalicious
      |> Arb.fromGen
      |> Prop.forAll <| fun xml ->
          let doc = XmlDocument () in
              doc.LoadXml xml
    testProperty "XML malicious injection" <| fun () ->
      let xpath = "/Person/Age"
      let doc = XmlDocument ()
      doc.LoadXml "<Person><Age/></Person>"

      [ xpath, Arb.generate<PositiveInt> |> Gen.map (fun p -> p.Get) ]
      |> Map.ofList
      |> FSec.xmlMaliciousInject doc
      |> Arb.fromGen
      |> Prop.forAll <| fun mal ->
          mal.SelectSingleNode xpath
          |> fun n -> n <> null .&. (int n.InnerText > 0)
  ]

let stubQueryParams =
    [ "category", Arb.generate<string>
      "style", Arb.generate<string>
      "size", Arb.generate<string> ]
    |> Map.ofList

module Regex = let isMatch p x = Regex.IsMatch (x, p)

[<Tests>]
let url_tests =
  testList "URL tests" [
    testProperty "URL tampered generation" <| fun () ->
      stubQueryParams
      |> FSec.urlTampered "http://example.com"
      |> Arb.fromGen
      |> Prop.forAll <| fun url ->
          url.Split '&' 
          |> Array.length
          |> (<=!) 3;

    testProperty "URL bogus query parameter generation" <| fun () ->
      stubQueryParams
      |> FSec.urlBogus "http://example.com"
      |> Arb.fromGen
      |> Prop.forAll <| fun url ->
          let qs = url.Split '&'
          [ "category"; "style"; "size" ] 
          |> List.forall (fun x ->
              Array.exists (fun (s : string) -> s.Contains x) qs)
          .&. (qs.Length > 3)
  ]

let regexTraversal = "^[(..|%252e%252e)|(\/|%2f)|(\\|%5c)]+"
let (|Guid|) (g : Guid) = g.ToString()

[<Tests>]
let file_tests =
    testList "file system tests" [
      testCase "create file of 1 MB" <| fun () ->
        Environment.CurrentDirectory
        |> DirectoryInfo
        |> FSec.fileOfSize 1 MB
        |> fun file ->
            file.Length =! (int64 (1024 * 1024) + 1L)

      testProperty "creates path dir traversal" <| fun () ->
        FSec.pathDirTraversal
        |> Arb.fromGen
        |> Prop.forAll <| 
            Regex.isMatch regexTraversal

      testProperty "creates path fixed file traversal" <| fun (Guid name) ->
        FSec.pathFixedFileTraversal [name]
        |> Arb.fromGen
        |> Prop.forAll <| fun x ->
            Regex.isMatch regexTraversal x
            .&. x.Contains name

      testProperty "create path random file traversal" <| fun () ->
        FSec.pathFileTraversal ".txt"
        |> Arb.fromGen
        |> Prop.forAll <|
            Regex.isMatch (regexTraversal + "(.+).txt$")

      testPropertyWithConfig ({FsCheckConfig.defaultConfig with maxTest = 1 }) "create zip bomb" <| fun () ->
        Environment.CurrentDirectory
        |> DirectoryInfo
        |> FSec.fileOfSize 1 MB
        |> FSec.zipBombDepthWidth 1 2
        |> fun path ->
            use zip = new ZipFile (path)
            [ for z in zip -> z :?> ZipEntry ]
            |> List.forall (fun e -> e.Name.EndsWith ".zip") 
            .&. (zip.Count =! 2L)
    ]

[<Tests>]
let fuzz_tests =
    testList "fuzz tests" [
      testCase "weak passwords will be found in dictionary attack" <| fun () ->
        [ "qwerty"; "password" ]
        |> List.forall (fun x -> Seq.contains x Fuzz.johnTheRipper)
        |> Expect.isTrue <| "should contain weak passwords"
      testCase "change case of input strings" <| fun () ->
        let xs = Fuzz.case ".php"
        Expect.contains xs ".php" "fuzzed case should contain original input"
        Expect.hasLength xs 16 "fuzzed case should contain 16 different cases"
      testCase "change encoding of input strings" <| fun () ->
        let xs = Fuzz.encodingFrom Encoding.Unicode "👍"
        Expect.contains xs "??" "fuzzed encoding should not always succeed in converting"
        let xs = Fuzz.encoding "some input"
        Expect.allEqual xs "some input" "fuzzed encoding should succeed for all charaters"
    ]

[<PTests>]
let api_tests =
  testList "api tests" [
    testCaseAsync "run scan" <| async {
      let req =
        Req.endpoint PUT "http://localhost:4000/api"
      
      let req =
        req |> Req.routes [ "monitor"; "relatedmessages" ]
      
      let resp4XX payload (res : Response) = async {
        if res.StatusCode = HttpStatusCode.BadRequest
           || res.StatusCode = HttpStatusCode.NotFound
           || res.StatusCode = HttpStatusCode.NoContent then return None
        else let v = Vuln.medium "Possible problem JSON -> XML deserialization" res payload
             return Some v }

      let have payload res =
        if Res.allow [ HttpStatusCode.BadRequest; HttpStatusCode.NotFound; HttpStatusCode.NoContent ] res
        then None
        else Some <| Vuln.medium "Possible problem" res payload

      let! vulnerabilities = 
        Api.inject Fuzz.alphanum_special
        |> Api.into (Req.parameter "id")
        |> Api.into (Req.parameter "direction")
        |> Api.shouldAsync resp4XX
        |> Api.scan req
      
      let err = String.Join (Environment.NewLine, vulnerabilities)
      Expect.isEmpty vulnerabilities err 
      () }
  ]
