#!/bin/bash

export RDBVERSION=$(cat ./rocksdbversion)
export REVISION=v${RDBVERSION}
export VERSION=$(cat ./version)
echo "REVISION = ${REVISION}"

export THISDIR="$(cd "$(dirname "$0")"; pwd -P)"

PATH=./bin:${PATH}

hash curl || { echo "curl is required, install curl"; exit 1; }
hash jq || {
	# If this is windows, the jq executable can be downloaded
	OSINFO=$(uname)
	if [[ $OSINFO == *"MSYS"* || $OSINFO == *"MINGW"* ]]; then
		echo "Windows detected, will attempt to download jq"
		mkdir -p bin
		curl --silent -L 'https://github.com/stedolan/jq/releases/download/jq-1.6/jq-win64.exe' -o bin/jq.exe
	fi
}
hash jq || { echo "jq is required, install jq"; exit 1; }

# These can be overridden in ~/.rocksdb-sharp-upload-info
# also use .netrc for github login credentials
GITHUB_LOGIN="warrenfalk"

if [ -f ~/.rocksdb-sharp-upload-info ]; then
	. ~/.rocksdb-sharp-upload-info
fi

cd $(dirname $0)

upload() {
	RELEASE="$1"
	FILE="$2"
	CONTENT_TYPE="$3"
	
	RELEASE_URL=`curl https://api.github.com/repos/warrenfalk/rocksdb-sharp-native/releases --netrc-file ~/.netrc | jq ".[]|{name: .name, url: .url}|select(.name == \"${RELEASE}\")|.url" --raw-output`
	echo "Release URL: ${RELEASE_URL}"
	if [ "${RELEASE_URL}" == "" ]; then
	echo "Creating Release..."
		PAYLOAD="{\"tag_name\": \"${RELEASE}\", \"target_commitish\": \"master\", \"name\": \"${RELEASE}\", \"body\": \"RocksDb native ${RELEASE} (rocksdb ${RDBVERSION})\", \"draft\": true, \"prelease\": false }"
		echo "Sending:"
		echo ${PAYLOAD}
		echo "-----------"
		export RELEASE_INFO=$(curl --silent -H "Content-Type: application/json" -X POST -d "${PAYLOAD}" --netrc-file ~/.netrc ${CURLOPTIONS} https://api.github.com/repos/warrenfalk/rocksdb-sharp-native/releases)
	else
		export RELEASE_INFO=`curl --silent -H "Content-Type: application/json" --netrc-file ~/.netrc ${CURLOPTIONS} ${RELEASE_URL}`
		#echo "Release response for ${RELEASE_URL}"
		#echo ${RELEASE_INFO}
	fi

	echo "Response:"
	echo ${RELEASE_INFO}
	echo "-----------"
	UPLOADURL="$(echo "${RELEASE_INFO}" | jq .upload_url --raw-output)"
	echo "Upload URL:"
	echo "${UPLOADURL}"
	echo "-----------"
	if [ "$UPLOADURL" == "null" ]; then
		echo "Release creation not successful or unable to determine upload url:"
		echo "${DRAFTINFO}"
		echo "-----------"
		exit 1;
	fi
	UPLOADURLBASE="${UPLOADURL%\{*\}}"
	echo "Uploading..."
	echo "to $UPLOADURLBASE"
	FILE_NAME=$(basename "$FILE")
	curl --progress-bar -H "Content-Type: ${CONTENT_TYPE}" -X POST --data-binary @${FILE} --netrc-file ~/.netrc ${CURLOPTIONS} ${UPLOADURLBASE}?name=${FILE_NAME}
}

MAC_LIB_FILE=./rocksdb-${REVISION}/osx-x64/native/librocksdb.dylib
if [ -f ${MAC_LIB_FILE} ]; then
	echo "Uploading MAC native"
	upload ${REVISION} ${MAC_LIB_FILE} 'application/octet-stream'
	ZIPFILE="${THISDIR}/rocksdb-${REVISION}-osx-x64.zip"
	rm -f ${ZIPFILE}
	(cd "$(dirname "${MAC_LIB_FILE}")" && zip -r "${ZIPFILE}" "$(basename "${MAC_LIB_FILE}")")
	upload ${REVISION} ${ZIPFILE} 'application/zip'
fi

WINDOWS_LIB_FILE=./rocksdb-${REVISION}/win-x64/native/rocksdb.dll
if [ -f ${WINDOWS_LIB_FILE} ]; then
	echo "Uploading Windows native"
	upload ${REVISION} ${WINDOWS_LIB_FILE} 'application/octet-stream'
	ZIPFILE="${THISDIR}/rocksdb-${REVISION}-win-x64.zip"
	rm -f ${ZIPFILE}
	(cd "$(dirname "${WINDOWS_LIB_FILE}")" && /c/Program\ Files/7-Zip/7z.exe a "${ZIPFILE}" "$(basename "${WINDOWS_LIB_FILE}")")
	upload ${REVISION} ${ZIPFILE} 'application/zip'
fi

LINUX_LIB_FILE=./rocksdb-${REVISION}/linux-x64/native/librocksdb.so
if [ -f ${LINUX_LIB_FILE} ]; then
	echo "Uploading Linux native"
	upload ${REVISION} ${LINUX_LIB_FILE} 'application/octet-stream'
	ZIPFILE="${THISDIR}/rocksdb-${REVISION}-linux-x64.zip"
	rm -f ${ZIPFILE}
	(cd "$(dirname "${LINUX_LIB_FILE}")" && zip -r "${ZIPFILE}" "$(basename "${LINUX_LIB_FILE}")")
	upload ${REVISION} ${ZIPFILE} 'application/zip'
fi



