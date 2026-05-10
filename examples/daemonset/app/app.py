import os
import requests
from flask import Flask, request, jsonify
import logging

app = Flask(__name__)
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# Get configuration from environment
NODE_IP = os.environ.get('NODE_IP', 'localhost')
GATEWAY_PORT = os.environ.get('GATEWAY_PORT', '8091')
QUEUE_ID = 'demo-queue'
GATEWAY_URL = f"http://{NODE_IP}:{GATEWAY_PORT}"

logger.info(f"Starting DaprMQ Example App")
logger.info(f"Gateway URL: {GATEWAY_URL}")
logger.info(f"Queue ID: {QUEUE_ID}")


@app.route('/')
def index():
    return f"""
    <!DOCTYPE html>
    <html>
    <head>
        <title>DaprMQ Python Example</title>
        <style>
            body {{ font-family: Arial, sans-serif; margin: 40px; }}
            code {{ background-color: #f4f4f4; padding: 2px 6px; border-radius: 3px; }}
            pre {{ background-color: #f4f4f4; padding: 15px; border-radius: 5px; overflow-x: auto; }}
        </style>
    </head>
    <body>
        <h1>DaprMQ Python Example</h1>
        <p><strong>Gateway URL:</strong> <code>{GATEWAY_URL}</code></p>
        <p><strong>Queue ID:</strong> <code>{QUEUE_ID}</code></p>

        <h2>API Endpoints</h2>
        <ul>
            <li><strong>GET /health</strong> - Health check and gateway connectivity</li>
            <li><strong>POST /push</strong> - Push item to queue</li>
            <li><strong>POST /pop</strong> - Pop item from queue</li>
        </ul>

        <h2>Example: Push Item</h2>
        <pre>curl -X POST http://&lt;nodeport-url&gt;/push \\
  -H "Content-Type: application/json" \\
  -d '{{"item": {{"task": "hello"}}, "priority": 1}}'</pre>

        <h2>Example: Pop Item</h2>
        <pre>curl -X POST http://&lt;nodeport-url&gt;/pop</pre>
    </body>
    </html>
    """


@app.route('/health', methods=['GET'])
def health():
    """Health check endpoint that also verifies gateway connectivity"""
    try:
        response = requests.get(f"{GATEWAY_URL}/health", timeout=2)
        response.raise_for_status()
        return jsonify({
            "status": "healthy",
            "gateway": "connected",
            "gateway_url": GATEWAY_URL,
            "queue_id": QUEUE_ID
        }), 200
    except requests.exceptions.RequestException as e:
        logger.error(f"Gateway health check failed: {e}")
        return jsonify({
            "status": "degraded",
            "gateway": "unreachable",
            "gateway_url": GATEWAY_URL,
            "error": str(e)
        }), 503


@app.route('/push', methods=['POST'])
def push():
    """
    Push an item to the DaprMQ queue

    Expected body:
    {
        "item": { ... },       # Any JSON object
        "priority": 1          # Optional, defaults to 1
    }
    """
    try:
        data = request.get_json()

        if not data:
            return jsonify({"error": "Request body must be JSON"}), 400

        if 'item' not in data:
            return jsonify({"error": "Missing required field 'item'"}), 400

        priority = data.get('priority', 1)

        # Transform to DaprMQ API format
        payload = {
            "items": [{
                "item": data['item'],
                "priority": priority
            }]
        }

        logger.info(f"Pushing item to queue {QUEUE_ID} with priority {priority}")

        response = requests.post(
            f"{GATEWAY_URL}/queue/{QUEUE_ID}/push",
            json=payload,
            timeout=5
        )

        logger.info(f"Push response: {response.status_code}")

        return jsonify(response.json()), response.status_code

    except requests.exceptions.Timeout:
        logger.error("Gateway timeout")
        return jsonify({"error": "Gateway timeout"}), 504
    except requests.exceptions.RequestException as e:
        logger.error(f"Gateway error: {e}")
        return jsonify({"error": "Gateway unreachable", "details": str(e)}), 503
    except Exception as e:
        logger.error(f"Unexpected error: {e}")
        return jsonify({"error": "Internal server error", "details": str(e)}), 500


@app.route('/pop', methods=['POST'])
def pop():
    """
    Pop an item from the DaprMQ queue

    No request body required
    """
    try:
        logger.info(f"Popping item from queue {QUEUE_ID}")

        response = requests.post(
            f"{GATEWAY_URL}/queue/{QUEUE_ID}/pop",
            timeout=5
        )

        logger.info(f"Pop response: {response.status_code}")

        # Handle empty queue (204 No Content)
        if response.status_code == 204:
            return jsonify({"items": [], "message": "Queue empty"}), 200

        return jsonify(response.json()), response.status_code

    except requests.exceptions.Timeout:
        logger.error("Gateway timeout")
        return jsonify({"error": "Gateway timeout"}), 504
    except requests.exceptions.RequestException as e:
        logger.error(f"Gateway error: {e}")
        return jsonify({"error": "Gateway unreachable", "details": str(e)}), 503
    except Exception as e:
        logger.error(f"Unexpected error: {e}")
        return jsonify({"error": "Internal server error", "details": str(e)}), 500


if __name__ == '__main__':
    app.run(host='0.0.0.0', port=5000)
