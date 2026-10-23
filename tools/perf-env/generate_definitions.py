#!/usr/bin/env python3
"""
Generates a RabbitMQ definitions file that mimics a set of NServiceBus systems using the
conventional routing topology of the RabbitMQ transport:

  * one quorum queue + one fanout exchange per endpoint (exchange -> queue)
  * one fanout exchange per event type, bound to the exchange of each subscribing endpoint
  * exchange-to-exchange bindings from concrete event types to their base interfaces
  * the v2 delayed-delivery infrastructure (28 levels of topic exchanges and quorum queues)
  * error/audit queues, a few legacy classic queues drained by dynamic shovels, one broken shovel

Output is deterministic for a given --seed, so runs are comparable.

Usage: generate_definitions.py [--vhosts 10] [--endpoints 30] [--events 80] [--out docker/rabbitmq/definitions.json]
"""
import argparse
import base64
import hashlib
import json
import random
import re
import urllib.parse

DOMAINS = ["sales", "billing", "shipping", "inventory", "crm", "payments", "notifications", "reporting", "identity", "catalog",
           "pricing", "returns", "loyalty", "search", "marketing", "support", "procurement", "warehouse", "fleet", "hr"]
NOUNS = ["Order", "Customer", "Invoice", "Payment", "Shipment", "Product", "Price", "Discount", "Return", "Notification",
         "Report", "Account", "Contract", "Quote", "Lead", "Subscription", "Refund", "LedgerEntry", "TaxRate", "Stock",
         "Carrier", "Parcel", "Email", "Sms", "Document", "Review", "Voucher", "Basket", "Supplier", "Employee"]
VERBS = ["Created", "Updated", "Deleted", "Approved", "Rejected", "Submitted", "Cancelled", "Completed", "Expired", "Archived"]
DELAY_LEVELS = 28
ADMIN_USER, ADMIN_PASSWORD = "dqe-admin", "dqe-admin"


def password_hash(password: str, rng: random.Random) -> str:
    # rabbit_password_hashing_sha256: base64(salt ++ sha256(salt ++ password))
    salt = bytes(rng.getrandbits(8) for _ in range(4))
    return base64.b64encode(salt + hashlib.sha256(salt + password.encode()).digest()).decode()


def pascal(name: str) -> str:
    return name[:1].upper() + name[1:]


def generate(vhost_count: int, endpoint_count: int, event_count: int, seed: int) -> dict:
    rng = random.Random(seed)
    defs = {
        "rabbit_version": "4.1.0",
        "users": [{"name": ADMIN_USER, "password_hash": password_hash(ADMIN_PASSWORD, rng),
                   "hashing_algorithm": "rabbit_password_hashing_sha256", "tags": ["administrator"]}],
        "vhosts": [], "permissions": [], "parameters": [], "policies": [],
        "queues": [], "exchanges": [], "bindings": [],
    }

    def queue(vhost, name, qtype="quorum", **args):
        defs["queues"].append({"name": name, "vhost": vhost, "durable": True, "auto_delete": False,
                               "arguments": {"x-queue-type": qtype, **args}})

    def exchange(vhost, name, etype="fanout"):
        defs["exchanges"].append({"name": name, "vhost": vhost, "type": etype, "durable": True,
                                  "auto_delete": False, "internal": False, "arguments": {}})

    def bind(vhost, source, destination, dtype="exchange", key=""):
        defs["bindings"].append({"source": source, "vhost": vhost, "destination": destination,
                                 "destination_type": dtype, "routing_key": key, "arguments": {}})

    vhosts = ["/"] + DOMAINS[:vhost_count]
    for vhost in vhosts:
        defs["vhosts"].append({"name": vhost})
        defs["permissions"].append({"user": ADMIN_USER, "vhost": vhost, "configure": ".*", "write": ".*", "read": ".*"})

    # The default vhost only holds a couple of plain, non-NServiceBus queues.
    queue("/", "legacy.inbox", "classic")
    queue("/", "legacy.outbox", "classic")

    for vhost in vhosts[1:]:
        domain = pascal(vhost)

        # Delayed delivery v2: level N dead-letters into level N-1, level 0 into the delivery exchange.
        exchange(vhost, "nsb.v2.delay-delivery", "topic")
        for level in range(DELAY_LEVELS):
            name = f"nsb.v2.delay-level-{level:02d}"
            exchange(vhost, name, "topic")
            dlx = f"nsb.v2.delay-level-{level - 1:02d}" if level else "nsb.v2.delay-delivery"
            queue(vhost, name, "quorum", **{"x-message-ttl": (2 ** level) * 1000, "x-dead-letter-exchange": dlx,
                                           "x-dead-letter-strategy": "at-least-once", "x-overflow": "reject-publish"})
            prefix = "*." * (DELAY_LEVELS - 1 - level)
            bind(vhost, name, name, "queue", f"{prefix}1.#")
            if level:
                bind(vhost, name, f"nsb.v2.delay-level-{level - 1:02d}", "exchange", f"{prefix}0.#")
        bind(vhost, "nsb.v2.delay-delivery", "nsb.v2.delay-level-27", "exchange", "#")

        queue(vhost, "error")
        queue(vhost, "audit")
        # error/audit have no NServiceBus policy, so RabbitMQ 4's default delivery-limit (20) would apply:
        # browsing them repeatedly would dead-letter messages. Endpoint queues get their own policy below.
        # Not a ".*" policy: a queue gets a single policy, and NServiceBus refuses to start an endpoint
        # whose input queue has a delivery limit other than its own (100000).
        defs["policies"].append({"vhost": vhost, "name": "dqe.unlimited-delivery", "pattern": "^(error|audit)$",
                                 "apply-to": "quorum_queues", "definition": {"delivery-limit": -1}, "priority": 0})

        nouns = rng.sample(NOUNS, min(endpoint_count, len(NOUNS)))
        endpoints = [f"{domain}.{n}Service" for n in nouns]
        endpoints += [f"{domain}.Worker{i:02d}" for i in range(max(0, endpoint_count - len(nouns)))]
        for endpoint in endpoints:
            queue(vhost, endpoint)
            exchange(vhost, endpoint)
            bind(vhost, endpoint, endpoint, "queue")
            # What NServiceBus.RabbitMQ 10+ creates at endpoint start (its "practically unlimited" value).
            defs["policies"].append({"vhost": vhost, "name": f"nsb.{endpoint}.delivery-limit",
                                     "pattern": "^" + re.escape(endpoint) + "$", "apply-to": "quorum_queues",
                                     "definition": {"delivery-limit": 100000}, "priority": 0})

        combos = [(n, v) for n in NOUNS for v in VERBS]
        rng.shuffle(combos)
        base_interfaces = sorted({n for n, _ in combos[:event_count]})[: max(1, event_count // 8)]
        for base in base_interfaces:
            exchange(vhost, f"{domain}.Messages.Events:I{base}Event")
        for noun, verb in combos[:event_count]:
            event = f"{domain}.Messages.Events:{noun}{verb}"
            exchange(vhost, event)
            if noun in base_interfaces:
                bind(vhost, event, f"{domain}.Messages.Events:I{noun}Event")
            for subscriber in rng.sample(endpoints, rng.randint(1, 5)):
                bind(vhost, event, subscriber)

        # Legacy classic queues drained into endpoints by dynamic shovels.
        local = "amqp:///" + urllib.parse.quote(vhost, safe="")
        for i, target in enumerate(rng.sample(endpoints, 2)):
            legacy = f"legacy.{vhost}.feed{i + 1}"
            queue(vhost, legacy, "classic")
            defs["parameters"].append({"vhost": vhost, "component": "shovel", "name": f"{legacy}-to-{target}", "value": {
                "src-protocol": "amqp091", "src-uri": local, "src-queue": legacy,
                "dest-protocol": "amqp091", "dest-uri": local, "dest-queue": target,
                "ack-mode": "on-confirm", "src-delete-after": "never",
                # Shovels may start before the queues are imported: never let them declare (classic) queues.
                "src-predeclared": True, "dest-predeclared": True}})

    # A shovel towards an unreachable broker, with credentials in the URI (must be masked by the explorer).
    defs["parameters"].append({"vhost": vhosts[1], "component": "shovel", "name": "archive-to-dr-site", "value": {
        "src-protocol": "amqp091", "src-uri": "amqp:///" + urllib.parse.quote(vhosts[1], safe=""), "src-queue": "audit",
        "dest-protocol": "amqp091", "dest-uri": "amqp://shovel-user:S3cr3t-Passw0rd@dr-site.invalid:5672/archive",
        "dest-queue": "audit-archive", "ack-mode": "on-confirm", "src-delete-after": "never",
        "src-predeclared": True, "dest-predeclared": True, "reconnect-delay": 30}})
    return defs


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--vhosts", type=int, default=10)
    parser.add_argument("--endpoints", type=int, default=30)
    parser.add_argument("--events", type=int, default=80)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--out", default="docker/rabbitmq/definitions.json")
    args = parser.parse_args()

    defs = generate(min(args.vhosts, len(DOMAINS)), args.endpoints, args.events, args.seed)
    with open(args.out, "w") as f:
        json.dump(defs, f, indent=1)
    print(f"{args.out}: {len(defs['vhosts'])} vhosts, {len(defs['queues'])} queues, {len(defs['exchanges'])} exchanges, "
          f"{len(defs['bindings'])} bindings, {len(defs['parameters'])} shovels, {len(defs['policies'])} policies")


if __name__ == "__main__":
    main()
