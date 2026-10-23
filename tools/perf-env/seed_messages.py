#!/usr/bin/env python3
"""
Publishes NServiceBus-shaped sample messages into the topology created by generate_definitions.py:
failed messages in `error`, processed messages in `audit`, pending messages in some endpoint queues,
plus non-NServiceBus, XML and binary messages to exercise the viewer.

Uses the management API (one HTTP call per message) with a small thread pool; meant for local seeding only.

Usage: seed_messages.py [--url http://localhost:15672] [--per-queue 30] [--seed 7]
"""
import argparse
import base64
import json
import random
import urllib.parse
import urllib.request
import uuid
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone

USER, PASSWORD = "dqe-admin", "dqe-admin"
EXCEPTIONS = [
    ("System.InvalidOperationException", "Order {id} cannot be shipped: the order is not in state 'Paid'."),
    ("System.TimeoutException", "The operation has timed out while calling the pricing service."),
    ("Microsoft.Data.SqlClient.SqlException", "Violation of PRIMARY KEY constraint 'PK_Invoices'. Cannot insert duplicate key."),
    ("System.Text.Json.JsonException", "The JSON value could not be converted to System.Decimal. Path: $.Amount"),
]


def nsb_time(dt: datetime) -> str:
    return dt.strftime("%Y-%m-%d %H:%M:%S:%f") + " Z"


class Api:
    def __init__(self, url):
        self.url = url.rstrip("/")
        self.auth = "Basic " + base64.b64encode(f"{USER}:{PASSWORD}".encode()).decode()

    def call(self, method, path, body=None):
        req = urllib.request.Request(self.url + path, method=method,
                                     data=json.dumps(body).encode() if body is not None else None,
                                     headers={"Authorization": self.auth, "Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=30) as resp:
            return json.loads(resp.read() or b"null")

    def publish(self, vhost, queue, payload, properties, binary=False):
        body = {"properties": properties, "routing_key": queue,
                "payload": base64.b64encode(payload).decode() if binary else payload,
                "payload_encoding": "base64" if binary else "string"}
        result = self.call("POST", f"/api/exchanges/{urllib.parse.quote(vhost, safe='')}/amq.default/publish", body)
        if not result.get("routed"):
            raise RuntimeError(f"not routed: {vhost}/{queue}")


def nsb_message(rng, vhost, endpoints, now, failed=False, audited=False, xml=False):
    domain = vhost.capitalize()
    sender, receiver = rng.sample(endpoints, 2)
    noun = receiver.split(".")[1].replace("Service", "")
    event = f"{noun}{rng.choice(['Created', 'Submitted', 'Approved', 'Cancelled'])}"
    intent = rng.choice(["Publish", "Send"])
    entity_id = str(uuid.UUID(int=rng.getrandbits(128)))
    message_id = str(uuid.UUID(int=rng.getrandbits(128)))
    sent = now - timedelta(minutes=rng.randint(1, 60 * 24 * 3), microseconds=rng.randint(0, 999999))
    type_name = f"{domain}.Messages.Events.{event}, {domain}.Messages, Version=1.4.0.0, Culture=neutral, PublicKeyToken=null"
    if rng.random() < 0.3:
        type_name += f";{domain}.Messages.Events.I{noun}Event, {domain}.Messages, Version=1.4.0.0, Culture=neutral, PublicKeyToken=null"

    headers = {
        "NServiceBus.MessageId": message_id,
        "NServiceBus.MessageIntent": intent,
        "NServiceBus.ConversationId": str(uuid.UUID(int=rng.getrandbits(128))),
        "NServiceBus.CorrelationId": message_id,
        "NServiceBus.OriginatingEndpoint": sender,
        "NServiceBus.OriginatingMachine": f"{vhost}-app-0{rng.randint(1, 4)}",
        "NServiceBus.ReplyToAddress": sender,
        "NServiceBus.ContentType": "text/xml" if xml else "application/json",
        "NServiceBus.EnclosedMessageTypes": type_name,
        "NServiceBus.Version": rng.choice(["8.2.4", "9.2.3"]),
        "NServiceBus.TimeSent": nsb_time(sent),
        "$.diagnostics.originating.hostid": uuid.UUID(int=rng.getrandbits(128)).hex,
        "$.diagnostics.hostdisplayname": f"{vhost}-app-0{rng.randint(1, 4)}",
        "traceparent": f"00-{rng.getrandbits(128):032x}-{rng.getrandbits(64):016x}-01",
    }
    if rng.random() < 0.2:
        headers["NServiceBus.SagaId"] = str(uuid.UUID(int=rng.getrandbits(128)))
        headers["NServiceBus.OriginatingSagaType"] = f"{domain}.Sagas.{noun}Policy, {domain}.Endpoint"
    if audited or failed:
        started = sent + timedelta(milliseconds=rng.randint(5, 4000))
        headers.update({
            "NServiceBus.ProcessingEndpoint": receiver,
            "NServiceBus.ProcessingMachine": f"{vhost}-worker-0{rng.randint(1, 6)}",
            "NServiceBus.ProcessingStarted": nsb_time(started),
            "NServiceBus.ProcessingEnded": nsb_time(started + timedelta(milliseconds=rng.randint(2, 900))),
        })
    if failed:
        exc_type, exc_message = rng.choice(EXCEPTIONS)
        exc_message = exc_message.format(id=entity_id)
        headers.update({
            "NServiceBus.ExceptionInfo.ExceptionType": exc_type,
            "NServiceBus.ExceptionInfo.Message": exc_message,
            "NServiceBus.ExceptionInfo.Source": f"{domain}.Endpoint",
            "NServiceBus.ExceptionInfo.HelpLink": "",
            "NServiceBus.ExceptionInfo.StackTrace": (
                f"{exc_type}: {exc_message}\n"
                f"   at {domain}.Endpoint.Handlers.{event}Handler.Handle({event} message, IMessageHandlerContext context) in /src/{domain}/Handlers/{event}Handler.cs:line {rng.randint(20, 180)}\n"
                "   at NServiceBus.Pipeline.MessageHandler.Invoke(Object message, IMessageHandlerContext handlerContext)\n"
                "   at NServiceBus.InvokeHandlerTerminator.Terminate(IInvokeHandlerContext context)\n"
                "   at NServiceBus.LoadHandlersConnector.Invoke(IIncomingLogicalMessageContext context, Func`2 stage)"),
            "NServiceBus.FailedQ": receiver,
            "NServiceBus.TimeOfFailure": nsb_time(sent + timedelta(seconds=rng.randint(30, 900))),
            # As NServiceBus 10 writes them: the retry counter is removed when moving to the error queue,
            # only the timestamp of the last delayed retry is kept; Exception.Data entries are prefixed.
            "NServiceBus.Retries.Timestamp": nsb_time(sent + timedelta(seconds=rng.randint(10, 300))),
            "NServiceBus.ExceptionInfo.Data.Handler type": f"{domain}.Endpoint.Handlers.{event}Handler",
            "NServiceBus.ExceptionInfo.Data.Message type": f"{domain}.Messages.Events.{event}",
            "NServiceBus.ExceptionInfo.Data.Message ID": message_id,
            "NServiceBus.ExceptionInfo.Data.Handler canceled": "False",
            "NServiceBus.ExceptionInfo.Data.Pipeline canceled": "False",
            f"NServiceBus.ExceptionInfo.Data.{noun}Id": entity_id,
            "x-last-death-exchange": "nsb.v2.delay-level-02",
            "x-last-death-queue": "nsb.v2.delay-level-02",
            "x-last-death-reason": "expired",
        })

    payload_obj = {f"{noun}Id": entity_id, "CustomerId": rng.randint(1000, 99999),
                   "Amount": round(rng.uniform(5, 5000), 2), "Currency": "EUR",
                   "Lines": [{"Sku": f"SKU-{rng.randint(10000, 99999)}", "Quantity": rng.randint(1, 9)} for _ in range(rng.randint(1, 4))],
                   "OccurredAt": sent.isoformat()}
    if xml:
        lines = "".join(f"<Line><Sku>{l['Sku']}</Sku><Quantity>{l['Quantity']}</Quantity></Line>" for l in payload_obj["Lines"])
        payload = (f'<?xml version="1.0"?><{event} xmlns="http://tempuri.net/{domain}.Messages.Events">'
                   f"<{noun}Id>{entity_id}</{noun}Id><Amount>{payload_obj['Amount']}</Amount><Lines>{lines}</Lines></{event}>")
    else:
        payload = json.dumps(payload_obj, separators=(",", ":"))

    properties = {"delivery_mode": 2, "message_id": message_id, "correlation_id": message_id,
                  "content_type": headers["NServiceBus.ContentType"], "headers": headers}
    return payload, properties


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://localhost:15672")
    parser.add_argument("--per-queue", type=int, default=30)
    parser.add_argument("--seed", type=int, default=7)
    args = parser.parse_args()

    api = Api(args.url)
    rng = random.Random(args.seed)
    now = datetime.now(timezone.utc)
    queues = api.call("GET", "/api/queues?columns=name,vhost")
    by_vhost = {}
    for q in queues:
        by_vhost.setdefault(q["vhost"], []).append(q["name"])

    jobs = []
    for vhost, names in sorted(by_vhost.items()):
        endpoints = sorted(n for n in names if "." in n and not n.startswith(("nsb.", "legacy.")))
        if len(endpoints) < 2:
            for name in names:
                for i in range(5):
                    jobs.append((vhost, name, f"plain text message #{i} for {name}", {"delivery_mode": 2, "content_type": "text/plain"}, False))
            continue
        for _ in range(args.per_queue + 10):
            jobs.append((vhost, "error", *nsb_message(rng, vhost, endpoints, now, failed=True, xml=rng.random() < 0.1), False))
        for _ in range(args.per_queue + 20):
            jobs.append((vhost, "audit", *nsb_message(rng, vhost, endpoints, now, audited=True), False))
        for endpoint in rng.sample(endpoints, 5):
            for _ in range(args.per_queue):
                jobs.append((vhost, endpoint, *nsb_message(rng, vhost, endpoints, now), False))
        jobs.append((vhost, endpoints[0], json.dumps({"hello": "not an NServiceBus message"}),
                     {"content_type": "application/json", "headers": {"x-source": "curl"}}, False))
        jobs.append((vhost, endpoints[0], bytes(rng.getrandbits(8) for _ in range(256)),
                     {"content_type": "application/octet-stream"}, True))

    with ThreadPoolExecutor(max_workers=16) as pool:
        list(pool.map(lambda j: api.publish(*j), jobs))
    print(f"published {len(jobs)} messages into {len(by_vhost)} vhosts")


if __name__ == "__main__":
    main()
